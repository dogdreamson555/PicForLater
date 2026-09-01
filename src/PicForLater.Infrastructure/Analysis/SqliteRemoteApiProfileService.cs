using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Analysis;

public sealed class SqliteRemoteApiProfileService : IRemoteApiProfileService, IDisposable
{
    private const int MaximumIdentifierLength = 200;
    private const int MaximumDisplayNameLength = 200;
    private const int MaximumStatementLength = 4_000;
    private const int MaximumTextChars = 1_000_000;
    private const long MaximumImageBytes = 50L * 1024 * 1024;
    private const int MaximumOutputTokens = 32_768;
    private const int MaximumTimeoutSeconds = 600;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly AppDataPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _disposed;

    public SqliteRemoteApiProfileService(
        AppDataPaths paths,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RemoteAnalysisExecutionState> GetExecutionStateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                settings.ExecutionBackend,
                settings.RemoteInputMode,
                settings.RemoteApiProfileId,
                settings.OutputLanguage,
                settings.ProfileRevision,
                settings.UpdatedAtUtc,
                {ProfileColumns("profile")}
            FROM AnalysisSettings settings
            LEFT JOIN RemoteApiProfiles profile
                ON profile.ProfileId = settings.RemoteApiProfileId
            WHERE settings.Id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The analysis settings row is missing.");
        }

        var settings = new AnalysisExecutionSettings(
            (AnalysisExecutionBackend)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : (RemoteInputMode)reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            (AnalysisOutputLanguage)reader.GetInt32(3),
            reader.GetInt64(4),
            ParseDate(reader.GetString(5)));
        var profile = reader.IsDBNull(6) ? null : ReadProfile(reader, 6);
        return new RemoteAnalysisExecutionState(settings, profile);
    }

    public async Task<IReadOnlyList<RemoteApiProfile>> GetProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {ProfileColumns()}
            FROM RemoteApiProfiles
            ORDER BY DisplayName COLLATE NOCASE, ProfileId;
            """;
        var profiles = new List<RemoteApiProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async Task<RemoteApiProfile?> GetProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        profileId = NormalizeRequired(profileId, nameof(profileId), MaximumIdentifierLength);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadProfileAsync(
            connection,
            transaction: null,
            profileId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteApiProfile> SaveProfileAsync(
        RemoteApiProfile profile,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(profile);
        var candidate = profile with
        {
            ProfileId = NormalizeRequired(
                profile.ProfileId,
                nameof(profile.ProfileId),
                MaximumIdentifierLength),
        };
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var existing = await ReadProfileAsync(
                connection,
                transaction,
                candidate.ProfileId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var validationScopeChanged = ValidationScopeChanged(existing, candidate);
                var consentScopeChanged = ConsentScopeChanged(existing, candidate);
                if (validationScopeChanged)
                {
                    candidate = candidate with
                    {
                        ValidationState = RemoteApiProfileValidationState.Unverified,
                        LastVerifiedAtUtc = null,
                    };
                }

                if (validationScopeChanged || consentScopeChanged)
                {
                    candidate = candidate with
                    {
                        ConsentedInputMode = null,
                        ConsentedDisclosureVersion = null,
                        ConsentGrantedAtUtc = null,
                    };
                }
            }

            var normalized = NormalizeAndValidate(candidate);
            var now = _timeProvider.GetUtcNow();
            normalized = normalized with
            {
                UpdatedAtUtc = now,
            };
            await using var settingsCommand = connection.CreateCommand();
            settingsCommand.Transaction = transaction;
            settingsCommand.CommandText =
                """
                SELECT ExecutionBackend, RemoteInputMode, RemoteApiProfileId
                FROM AnalysisSettings WHERE Id = 1;
                """;
            AnalysisExecutionBackend backend;
            RemoteInputMode? inputMode;
            string? selectedProfileId;
            await using (var reader = await settingsCommand.ExecuteReaderAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The analysis settings row is missing.");
                }

                backend = (AnalysisExecutionBackend)reader.GetInt32(0);
                inputMode = reader.IsDBNull(1) ? null : (RemoteInputMode)reader.GetInt32(1);
                selectedProfileId = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            var isSelected = backend == AnalysisExecutionBackend.RemoteApi
                && selectedProfileId == normalized.ProfileId;
            await UpsertProfileAsync(
                connection,
                transaction,
                normalized,
                cancellationToken).ConfigureAwait(false);
            if (isSelected)
            {
                if (GetSelectionError(normalized, inputMode) is not null)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE AnalysisSettings
                        SET ExecutionBackend = @local,
                            ProfileRevision = ProfileRevision + 1,
                            UpdatedAtUtc = @updated
                        WHERE Id = 1;
                        """,
                        cancellationToken,
                        ("@local", (int)AnalysisExecutionBackend.Local),
                        ("@updated", ToDb(now))).ConfigureAwait(false);
                }
                else
                {
                    await IncrementRevisionAsync(
                        connection,
                        transaction,
                        now,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return normalized;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task DeleteProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        profileId = NormalizeRequired(profileId, nameof(profileId), MaximumIdentifierLength);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using (var selectedCommand = connection.CreateCommand())
            {
                selectedCommand.Transaction = transaction;
                selectedCommand.CommandText =
                    """
                    SELECT COUNT(*) FROM AnalysisSettings
                    WHERE Id = 1
                      AND ExecutionBackend = @remote
                      AND RemoteApiProfileId = @profileId;
                    """;
                selectedCommand.Parameters.AddWithValue("@remote", (int)AnalysisExecutionBackend.RemoteApi);
                selectedCommand.Parameters.AddWithValue("@profileId", profileId);
                var selected = Convert.ToInt32(
                    await selectedCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (selected != 0)
                {
                    throw new RemoteApiProfileException(
                        "remote.selected-profile-cannot-be-deleted");
                }
            }

            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM RemoteApiProfiles WHERE ProfileId = @profileId;",
                cancellationToken,
                ("@profileId", profileId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SetOutputLanguageAsync(
        AnalysisOutputLanguage outputLanguage,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(outputLanguage))
        {
            throw new ArgumentOutOfRangeException(nameof(outputLanguage));
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT OutputLanguage
                FROM AnalysisSettings WHERE Id = 1;
                """;
            var current = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                throw new InvalidDataException("The analysis settings row is missing.");
            }

            if ((AnalysisOutputLanguage)Convert.ToInt32(current, CultureInfo.InvariantCulture)
                == outputLanguage)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var now = _timeProvider.GetUtcNow();
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE AnalysisSettings
                SET OutputLanguage = @outputLanguage,
                    ProfileRevision = ProfileRevision + 1,
                    UpdatedAtUtc = @updated
                WHERE Id = 1;
                """,
                cancellationToken,
                ("@outputLanguage", (int)outputLanguage),
                ("@updated", ToDb(now))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SelectLocalAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT ExecutionBackend
                FROM AnalysisSettings WHERE Id = 1;
                """;
            AnalysisExecutionBackend currentBackend;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The analysis settings row is missing.");
                }

                currentBackend = (AnalysisExecutionBackend)reader.GetInt32(0);
            }

            if (currentBackend == AnalysisExecutionBackend.Local)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var now = _timeProvider.GetUtcNow();
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE AnalysisSettings
                SET ExecutionBackend = @local,
                    ProfileRevision = ProfileRevision + 1,
                    UpdatedAtUtc = @updated
                WHERE Id = 1;
                """,
                cancellationToken,
                ("@local", (int)AnalysisExecutionBackend.Local),
                ("@updated", ToDb(now))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SelectRemoteAsync(
        string profileId,
        RemoteInputMode inputMode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        profileId = NormalizeRequired(profileId, nameof(profileId), MaximumIdentifierLength);
        if (!Enum.IsDefined(inputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(inputMode));
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var profile = await ReadProfileAsync(
                connection,
                transaction,
                profileId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new RemoteApiProfileException("remote.profile-not-found");
            var selectionError = GetSelectionError(profile, inputMode);
            if (selectionError is not null)
            {
                throw new RemoteApiProfileException(selectionError);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT ExecutionBackend, RemoteInputMode, RemoteApiProfileId
                FROM AnalysisSettings WHERE Id = 1;
                """;
            AnalysisExecutionBackend currentBackend;
            RemoteInputMode? currentMode;
            string? currentProfileId;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The analysis settings row is missing.");
                }

                currentBackend = (AnalysisExecutionBackend)reader.GetInt32(0);
                currentMode = reader.IsDBNull(1) ? null : (RemoteInputMode)reader.GetInt32(1);
                currentProfileId = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            if (currentBackend == AnalysisExecutionBackend.RemoteApi
                && currentMode == inputMode
                && currentProfileId == profileId)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var now = _timeProvider.GetUtcNow();
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE AnalysisSettings
                SET ExecutionBackend = @remote,
                    RemoteInputMode = @inputMode,
                    RemoteApiProfileId = @profileId,
                    ProfileRevision = ProfileRevision + 1,
                    UpdatedAtUtc = @updated
                WHERE Id = 1;
                """,
                cancellationToken,
                ("@remote", (int)AnalysisExecutionBackend.RemoteApi),
                ("@inputMode", (int)inputMode),
                ("@profileId", profileId),
                ("@updated", ToDb(now))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutationGate.Dispose();
    }

    private static string? GetSelectionError(
        RemoteApiProfile profile,
        RemoteInputMode? inputMode)
    {
        if (!profile.IsEnabled)
        {
            return "remote.profile-disabled";
        }

        if (profile.ValidationState != RemoteApiProfileValidationState.Valid
            || profile.LastVerifiedAtUtc is null)
        {
            return "remote.profile-not-verified";
        }

        if (inputMode is null || !profile.SupportedInputModes.Contains(inputMode.Value))
        {
            return "remote.input-mode-not-supported";
        }

        if (profile.ConsentedInputMode != inputMode
            || profile.ConsentedDisclosureVersion != profile.DisclosureVersion
            || profile.ConsentGrantedAtUtc is null)
        {
            return "remote.consent-required";
        }

        return null;
    }

    private RemoteApiProfile NormalizeAndValidate(RemoteApiProfile profile)
    {
        var supportedInputModes = profile.SupportedInputModes?
            .Distinct()
            .Order()
            .ToArray()
            ?? [];
        if (supportedInputModes.Length == 0
            || supportedInputModes.Any(mode => !Enum.IsDefined(mode)))
        {
            throw new RemoteApiProfileException("remote.supported-input-modes-invalid");
        }

        if (profile.MaxTextChars is <= 0 or > MaximumTextChars
            || profile.MaxImageBytes is <= 0 or > MaximumImageBytes
            || profile.MaxOutputTokens is <= 0 or > MaximumOutputTokens
            || profile.TimeoutSeconds is <= 0 or > MaximumTimeoutSeconds)
        {
            throw new RemoteApiProfileException("remote.profile-limits-invalid");
        }

        if (!Enum.IsDefined(profile.Protocol)
            || !Enum.IsDefined(profile.AuthenticationKind)
            || !Enum.IsDefined(profile.StructuredOutputMode)
            || !Enum.IsDefined(profile.EndpointTrustMode)
            || !Enum.IsDefined(profile.ReasoningMode)
            || !Enum.IsDefined(profile.ReasoningWireFormat))
        {
            throw new RemoteApiProfileException("remote.protocol-settings-invalid");
        }

        if (profile.Protocol == RemoteApiProtocol.AnthropicMessages
            && profile.StructuredOutputMode is not RemoteStructuredOutputMode.JsonSchema
                and not RemoteStructuredOutputMode.PromptOnly)
        {
            throw new RemoteApiProfileException("remote.protocol-settings-invalid");
        }

        if (profile.Protocol != RemoteApiProtocol.OpenAiChatCompletions
            && (profile.ReasoningMode != RemoteReasoningMode.ProviderDefault
                || profile.ReasoningWireFormat != RemoteReasoningWireFormat.None))
        {
            throw new RemoteApiProfileException("remote.protocol-settings-invalid");
        }

        if ((profile.ReasoningWireFormat == RemoteReasoningWireFormat.None
                && profile.ReasoningMode != RemoteReasoningMode.ProviderDefault)
            || (profile.ReasoningWireFormat is RemoteReasoningWireFormat.ThinkingObject
                    or RemoteReasoningWireFormat.EnableThinkingBoolean
                    or RemoteReasoningWireFormat.ReasoningEnabledObject
                && profile.ReasoningMode is not RemoteReasoningMode.ProviderDefault
                    and not RemoteReasoningMode.Disabled))
        {
            throw new RemoteApiProfileException("remote.protocol-settings-invalid");
        }

        var baseUri = RemoteEndpointPolicy.IsAllowed(profile.BaseUri, profile.EndpointTrustMode)
            ? profile.BaseUri
            : throw new RemoteApiProfileException("remote.base-uri-invalid");
        var privacyUri = ValidateHttpsUri(profile.PrivacyUrl, disallowQuery: false, "remote.privacy-uri-invalid");
        var termsUri = ValidateHttpsUri(profile.TermsUrl, disallowQuery: false, "remote.terms-uri-invalid");
        if (!Enum.IsDefined(profile.ValidationState))
        {
            throw new RemoteApiProfileException("remote.validation-state-invalid");
        }

        if (profile.ValidationState == RemoteApiProfileValidationState.Valid
            && profile.LastVerifiedAtUtc is null)
        {
            throw new RemoteApiProfileException("remote.valid-profile-missing-verification-time");
        }

        var consentValues = new object?[]
        {
            profile.ConsentedInputMode,
            profile.ConsentedDisclosureVersion,
            profile.ConsentGrantedAtUtc,
        };
        var hasAnyConsentValue = consentValues.Any(value => value is not null);
        var hasAllConsentValues = consentValues.All(value => value is not null);
        if (hasAnyConsentValue != hasAllConsentValues)
        {
            throw new RemoteApiProfileException("remote.consent-state-incomplete");
        }

        if (hasAllConsentValues
            && (!Enum.IsDefined(profile.ConsentedInputMode!.Value)
                || !supportedInputModes.Contains(profile.ConsentedInputMode.Value)))
        {
            throw new RemoteApiProfileException("remote.consent-state-invalid");
        }

        var disclosureVersion = NormalizeRequired(
            profile.DisclosureVersion,
            nameof(profile.DisclosureVersion),
            MaximumIdentifierLength);
        var consentedDisclosureVersion = profile.ConsentedDisclosureVersion?.Trim();
        var consentMatchesDisclosure = hasAllConsentValues
            && string.Equals(
                consentedDisclosureVersion,
                disclosureVersion,
                StringComparison.Ordinal);
        return profile with
        {
            ProfileId = NormalizeRequired(profile.ProfileId, nameof(profile.ProfileId), MaximumIdentifierLength),
            ProviderId = NormalizeRequired(profile.ProviderId, nameof(profile.ProviderId), MaximumIdentifierLength),
            DisplayName = NormalizeRequired(profile.DisplayName, nameof(profile.DisplayName), MaximumDisplayNameLength),
            EndpointId = NormalizeRequired(profile.EndpointId, nameof(profile.EndpointId), MaximumIdentifierLength),
            BaseUri = baseUri,
            ModelId = NormalizeRequired(profile.ModelId, nameof(profile.ModelId), MaximumIdentifierLength),
            SupportedInputModes = supportedInputModes,
            PromptVersion = NormalizeRequired(profile.PromptVersion, nameof(profile.PromptVersion), MaximumIdentifierLength),
            OutputSchemaVersion = NormalizeRequired(
                profile.OutputSchemaVersion,
                nameof(profile.OutputSchemaVersion),
                MaximumIdentifierLength),
            PrivacyUrl = privacyUri,
            TermsUrl = termsUri,
            RetentionTrainingStatement = NormalizeRequired(
                profile.RetentionTrainingStatement,
                nameof(profile.RetentionTrainingStatement),
                MaximumStatementLength),
            RetentionTrainingVerifiedAtUtc = profile.RetentionTrainingVerifiedAtUtc.ToUniversalTime(),
            CredentialReference = NormalizeRequired(
                profile.CredentialReference,
                nameof(profile.CredentialReference),
                MaximumIdentifierLength),
            DisclosureVersion = disclosureVersion,
            ApiVersion = NormalizeOptional(profile.ApiVersion, nameof(profile.ApiVersion), MaximumIdentifierLength),
            LastVerifiedAtUtc = profile.LastVerifiedAtUtc?.ToUniversalTime(),
            ConsentedInputMode = consentMatchesDisclosure
                ? profile.ConsentedInputMode
                : null,
            ConsentedDisclosureVersion = consentMatchesDisclosure
                ? consentedDisclosureVersion
                : null,
            ConsentGrantedAtUtc = consentMatchesDisclosure
                ? profile.ConsentGrantedAtUtc?.ToUniversalTime()
                : null,
        };
    }

    private static bool ValidationScopeChanged(
        RemoteApiProfile existing,
        RemoteApiProfile current) =>
        !string.Equals(existing.ProviderId, current.ProviderId, StringComparison.Ordinal)
        || !string.Equals(existing.EndpointId, current.EndpointId, StringComparison.Ordinal)
        || !existing.BaseUri.Equals(current.BaseUri)
        || !string.Equals(existing.ModelId, current.ModelId, StringComparison.Ordinal)
        || !existing.SupportedInputModes.Order().SequenceEqual(
            (current.SupportedInputModes ?? []).Order())
        || !string.Equals(existing.PromptVersion, current.PromptVersion, StringComparison.Ordinal)
        || !string.Equals(existing.OutputSchemaVersion, current.OutputSchemaVersion, StringComparison.Ordinal)
        || existing.Protocol != current.Protocol
        || existing.AuthenticationKind != current.AuthenticationKind
        || existing.StructuredOutputMode != current.StructuredOutputMode
        || existing.EndpointTrustMode != current.EndpointTrustMode
        || !string.Equals(existing.ApiVersion, current.ApiVersion, StringComparison.Ordinal)
        || existing.DisableProviderFallbacks != current.DisableProviderFallbacks
        || existing.DisableExternalSearch != current.DisableExternalSearch
        || existing.ReasoningMode != current.ReasoningMode
        || existing.ReasoningWireFormat != current.ReasoningWireFormat
        || existing.MaxOutputTokens != current.MaxOutputTokens
        || existing.TimeoutSeconds != current.TimeoutSeconds
        || !string.Equals(
            existing.CredentialReference,
            current.CredentialReference,
            StringComparison.Ordinal);

    private static bool ConsentScopeChanged(
        RemoteApiProfile existing,
        RemoteApiProfile current) =>
        ValidationScopeChanged(existing, current)
        || existing.MaxTextChars != current.MaxTextChars
        || existing.MaxImageBytes != current.MaxImageBytes
        || !existing.PrivacyUrl.Equals(current.PrivacyUrl)
        || !existing.TermsUrl.Equals(current.TermsUrl)
        || !string.Equals(
            existing.RetentionTrainingStatement,
            current.RetentionTrainingStatement,
            StringComparison.Ordinal)
        || existing.RetentionTrainingVerifiedAtUtc != current.RetentionTrainingVerifiedAtUtc
        || !string.Equals(existing.DisclosureVersion, current.DisclosureVersion, StringComparison.Ordinal);

    private static Uri ValidateHttpsUri(Uri? uri, bool disallowQuery, string errorCode)
    {
        if (uri is null
            || !uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (disallowQuery && !string.IsNullOrEmpty(uri.Query)))
        {
            throw new RemoteApiProfileException(errorCode);
        }

        return uri;
    }

    private static string NormalizeRequired(string? value, string parameterName, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The value is missing or invalid.", parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeRequired(value, parameterName, maximumLength);
    }

    private async Task<RemoteApiProfile?> ReadProfileAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {ProfileColumns()}
            FROM RemoteApiProfiles
            WHERE ProfileId = @profileId;
            """;
        command.Parameters.AddWithValue("@profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProfile(reader)
            : null;
    }

    private static RemoteApiProfile ReadProfile(SqliteDataReader reader, int offset = 0)
    {
        var supportedInputModes = JsonSerializer.Deserialize<RemoteInputMode[]>(
                reader.GetString(offset + 6),
                JsonOptions)
            ?? throw new InvalidDataException("The remote API profile input modes are invalid.");
        return new RemoteApiProfile
        {
            ProfileId = reader.GetString(offset),
            ProviderId = reader.GetString(offset + 1),
            DisplayName = reader.GetString(offset + 2),
            EndpointId = reader.GetString(offset + 3),
            BaseUri = new Uri(reader.GetString(offset + 4), UriKind.Absolute),
            ModelId = reader.GetString(offset + 5),
            SupportedInputModes = supportedInputModes,
            PromptVersion = reader.GetString(offset + 7),
            OutputSchemaVersion = reader.GetString(offset + 8),
            MaxTextChars = reader.GetInt32(offset + 9),
            MaxImageBytes = reader.GetInt64(offset + 10),
            MaxOutputTokens = reader.GetInt32(offset + 11),
            TimeoutSeconds = reader.GetInt32(offset + 12),
            PrivacyUrl = new Uri(reader.GetString(offset + 13), UriKind.Absolute),
            TermsUrl = new Uri(reader.GetString(offset + 14), UriKind.Absolute),
            RetentionTrainingStatement = reader.GetString(offset + 15),
            RetentionTrainingVerifiedAtUtc = ParseDate(reader.GetString(offset + 16)),
            CredentialReference = reader.GetString(offset + 17),
            DisclosureVersion = reader.GetString(offset + 18),
            IsEnabled = reader.GetInt32(offset + 19) != 0,
            ValidationState = (RemoteApiProfileValidationState)reader.GetInt32(offset + 20),
            LastVerifiedAtUtc = reader.IsDBNull(offset + 21)
                ? null
                : ParseDate(reader.GetString(offset + 21)),
            ConsentedInputMode = reader.IsDBNull(offset + 22)
                ? null
                : (RemoteInputMode)reader.GetInt32(offset + 22),
            ConsentedDisclosureVersion = reader.IsDBNull(offset + 23)
                ? null
                : reader.GetString(offset + 23),
            ConsentGrantedAtUtc = reader.IsDBNull(offset + 24)
                ? null
                : ParseDate(reader.GetString(offset + 24)),
            UpdatedAtUtc = ParseDate(reader.GetString(offset + 25)),
            Protocol = (RemoteApiProtocol)reader.GetInt32(offset + 26),
            AuthenticationKind = (RemoteApiAuthenticationKind)reader.GetInt32(offset + 27),
            StructuredOutputMode = (RemoteStructuredOutputMode)reader.GetInt32(offset + 28),
            EndpointTrustMode = (RemoteEndpointTrustMode)reader.GetInt32(offset + 29),
            ApiVersion = reader.IsDBNull(offset + 30) ? null : reader.GetString(offset + 30),
            DisableProviderFallbacks = reader.GetInt32(offset + 31) != 0,
            DisableExternalSearch = reader.GetInt32(offset + 32) != 0,
            ReasoningMode = (RemoteReasoningMode)reader.GetInt32(offset + 33),
            ReasoningWireFormat = (RemoteReasoningWireFormat)reader.GetInt32(offset + 34),
        };
    }

    private static async Task UpsertProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RemoteApiProfile profile,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO RemoteApiProfiles (
                ProfileId, ProviderId, DisplayName, EndpointId, BaseUri, ModelId,
                SupportedInputModesJson, PromptVersion, OutputSchemaVersion,
                MaxTextChars, MaxImageBytes, MaxOutputTokens, TimeoutSeconds,
                PrivacyUrl, TermsUrl, RetentionTrainingStatement,
                RetentionTrainingVerifiedAtUtc, CredentialReference,
                DisclosureVersion, IsEnabled, ValidationState, LastVerifiedAtUtc,
                ConsentedInputMode, ConsentedDisclosureVersion, ConsentGrantedAtUtc,
                UpdatedAtUtc, Protocol, AuthenticationKind, StructuredOutputModeV2,
                EndpointTrustMode, ApiVersion, DisableProviderFallbacks,
                DisableExternalSearch, ReasoningMode, ReasoningWireFormat)
            VALUES (
                @profileId, @providerId, @displayName, @endpointId, @baseUri, @modelId,
                @inputModes, @promptVersion, @schemaVersion,
                @maxTextChars, @maxImageBytes, @maxOutputTokens, @timeoutSeconds,
                @privacyUrl, @termsUrl, @retentionStatement,
                @retentionVerified, @credentialReference,
                @disclosureVersion, @isEnabled, @validationState, @lastVerified,
                @consentedInputMode, @consentedVersion, @consentGranted,
                @updated, @protocol, @authenticationKind, @structuredOutputMode,
                @endpointTrustMode, @apiVersion, @disableProviderFallbacks,
                @disableExternalSearch, @reasoningMode, @reasoningWireFormat)
            ON CONFLICT(ProfileId) DO UPDATE SET
                ProviderId = excluded.ProviderId,
                DisplayName = excluded.DisplayName,
                EndpointId = excluded.EndpointId,
                BaseUri = excluded.BaseUri,
                ModelId = excluded.ModelId,
                SupportedInputModesJson = excluded.SupportedInputModesJson,
                PromptVersion = excluded.PromptVersion,
                OutputSchemaVersion = excluded.OutputSchemaVersion,
                MaxTextChars = excluded.MaxTextChars,
                MaxImageBytes = excluded.MaxImageBytes,
                MaxOutputTokens = excluded.MaxOutputTokens,
                TimeoutSeconds = excluded.TimeoutSeconds,
                PrivacyUrl = excluded.PrivacyUrl,
                TermsUrl = excluded.TermsUrl,
                RetentionTrainingStatement = excluded.RetentionTrainingStatement,
                RetentionTrainingVerifiedAtUtc = excluded.RetentionTrainingVerifiedAtUtc,
                CredentialReference = excluded.CredentialReference,
                DisclosureVersion = excluded.DisclosureVersion,
                IsEnabled = excluded.IsEnabled,
                ValidationState = excluded.ValidationState,
                LastVerifiedAtUtc = excluded.LastVerifiedAtUtc,
                ConsentedInputMode = excluded.ConsentedInputMode,
                ConsentedDisclosureVersion = excluded.ConsentedDisclosureVersion,
                ConsentGrantedAtUtc = excluded.ConsentGrantedAtUtc,
                UpdatedAtUtc = excluded.UpdatedAtUtc,
                Protocol = excluded.Protocol,
                AuthenticationKind = excluded.AuthenticationKind,
                StructuredOutputModeV2 = excluded.StructuredOutputModeV2,
                EndpointTrustMode = excluded.EndpointTrustMode,
                ApiVersion = excluded.ApiVersion,
                DisableProviderFallbacks = excluded.DisableProviderFallbacks,
                DisableExternalSearch = excluded.DisableExternalSearch,
                ReasoningMode = excluded.ReasoningMode,
                ReasoningWireFormat = excluded.ReasoningWireFormat;
            """,
            cancellationToken,
            ("@profileId", profile.ProfileId),
            ("@providerId", profile.ProviderId),
            ("@displayName", profile.DisplayName),
            ("@endpointId", profile.EndpointId),
            ("@baseUri", profile.BaseUri.AbsoluteUri),
            ("@modelId", profile.ModelId),
            ("@inputModes", JsonSerializer.Serialize(profile.SupportedInputModes, JsonOptions)),
            ("@promptVersion", profile.PromptVersion),
            ("@schemaVersion", profile.OutputSchemaVersion),
            ("@maxTextChars", profile.MaxTextChars),
            ("@maxImageBytes", profile.MaxImageBytes),
            ("@maxOutputTokens", profile.MaxOutputTokens),
            ("@timeoutSeconds", profile.TimeoutSeconds),
            ("@privacyUrl", profile.PrivacyUrl.AbsoluteUri),
            ("@termsUrl", profile.TermsUrl.AbsoluteUri),
            ("@retentionStatement", profile.RetentionTrainingStatement),
            ("@retentionVerified", ToDb(profile.RetentionTrainingVerifiedAtUtc)),
            ("@credentialReference", profile.CredentialReference),
            ("@disclosureVersion", profile.DisclosureVersion),
            ("@isEnabled", profile.IsEnabled ? 1 : 0),
            ("@validationState", (int)profile.ValidationState),
            ("@lastVerified", profile.LastVerifiedAtUtc is null
                ? null
                : ToDb(profile.LastVerifiedAtUtc.Value)),
            ("@consentedInputMode", profile.ConsentedInputMode is null
                ? null
                : (int)profile.ConsentedInputMode.Value),
            ("@consentedVersion", profile.ConsentedDisclosureVersion),
            ("@consentGranted", profile.ConsentGrantedAtUtc is null
                ? null
                : ToDb(profile.ConsentGrantedAtUtc.Value)),
            ("@updated", ToDb(profile.UpdatedAtUtc)),
            ("@protocol", (int)profile.Protocol),
            ("@authenticationKind", (int)profile.AuthenticationKind),
            ("@structuredOutputMode", (int)profile.StructuredOutputMode),
            ("@endpointTrustMode", (int)profile.EndpointTrustMode),
            ("@apiVersion", profile.ApiVersion),
            ("@disableProviderFallbacks", profile.DisableProviderFallbacks ? 1 : 0),
            ("@disableExternalSearch", profile.DisableExternalSearch ? 1 : 0),
            ("@reasoningMode", (int)profile.ReasoningMode),
            ("@reasoningWireFormat", (int)profile.ReasoningWireFormat)).ConfigureAwait(false);
    }

    private static Task IncrementRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE AnalysisSettings
            SET ProfileRevision = ProfileRevision + 1, UpdatedAtUtc = @updated
            WHERE Id = 1;
            """,
            cancellationToken,
            ("@updated", ToDb(updatedAtUtc)));

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _paths.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                ForeignKeys = true,
            }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ProfileColumns(string? alias = null)
    {
        var prefix = string.IsNullOrEmpty(alias) ? string.Empty : alias + ".";
        return $"""
            {prefix}ProfileId,
            {prefix}ProviderId,
            {prefix}DisplayName,
            {prefix}EndpointId,
            {prefix}BaseUri,
            {prefix}ModelId,
            {prefix}SupportedInputModesJson,
            {prefix}PromptVersion,
            {prefix}OutputSchemaVersion,
            {prefix}MaxTextChars,
            {prefix}MaxImageBytes,
            {prefix}MaxOutputTokens,
            {prefix}TimeoutSeconds,
            {prefix}PrivacyUrl,
            {prefix}TermsUrl,
            {prefix}RetentionTrainingStatement,
            {prefix}RetentionTrainingVerifiedAtUtc,
            {prefix}CredentialReference,
            {prefix}DisclosureVersion,
            {prefix}IsEnabled,
            {prefix}ValidationState,
            {prefix}LastVerifiedAtUtc,
            {prefix}ConsentedInputMode,
            {prefix}ConsentedDisclosureVersion,
            {prefix}ConsentGrantedAtUtc,
            {prefix}UpdatedAtUtc,
            {prefix}Protocol,
            {prefix}AuthenticationKind,
            {prefix}StructuredOutputModeV2,
            {prefix}EndpointTrustMode,
            {prefix}ApiVersion,
            {prefix}DisableProviderFallbacks,
            {prefix}DisableExternalSearch,
            {prefix}ReasoningMode,
            {prefix}ReasoningWireFormat
            """;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
