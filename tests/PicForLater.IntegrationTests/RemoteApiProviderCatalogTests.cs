using PicForLater.Analysis;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class RemoteApiProviderCatalogTests
{
    [Fact]
    public async Task StartupSync_ReadsExistingProfilesInSingleBatch()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        var countingProfiles = new CountingRemoteApiProfileService(profiles);

        await RemoteApiProviderCatalog.EnsureProfilesAsync(countingProfiles);

        Assert.Equal(1, countingProfiles.GetProfilesCallCount);
        Assert.Equal(0, countingProfiles.GetProfileCallCount);
    }

    [Fact]
    public async Task StartupSync_UpgradesSelectedLegacyPresetToV3AndReturnsExecutionToLocal()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);
        var preset = RemoteApiProviderCatalog.GetPreset("openai-official");
        var verifiedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        await ExecuteAsync(
            root.Paths.DatabasePath,
            """
            UPDATE RemoteApiProfiles
            SET BaseUri = @legacyBaseUri,
                PromptVersion = @legacyPromptVersion,
                ValidationState = @valid,
                LastVerifiedAtUtc = @verifiedAt,
                ConsentedInputMode = @inputMode,
                ConsentedDisclosureVersion = DisclosureVersion,
                ConsentGrantedAtUtc = @verifiedAt
            WHERE ProfileId = @profileId;

            UPDATE AnalysisSettings
            SET ExecutionBackend = @remote,
                RemoteInputMode = @inputMode,
                RemoteApiProfileId = @profileId
            WHERE Id = 1;
            """,
            ("@legacyBaseUri", "https://legacy-openai.example.test/v1/chat/completions"),
            ("@legacyPromptVersion", "picforlater.remote-analysis.v2"),
            ("@valid", (int)RemoteApiProfileValidationState.Valid),
            ("@verifiedAt", verifiedAt.ToString("O")),
            ("@inputMode", (int)RemoteInputMode.LocalOcrText),
            ("@profileId", preset.ProfileId),
            ("@remote", (int)AnalysisExecutionBackend.RemoteApi));

        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);

        var synchronized = await profiles.GetProfileAsync(preset.ProfileId);
        var execution = await profiles.GetExecutionStateAsync();
        Assert.NotNull(synchronized);
        Assert.Equal(preset.BaseUri, synchronized.BaseUri);
        Assert.Equal("picforlater.remote-analysis.v3", synchronized.PromptVersion);
        Assert.Equal(RemoteApiProfileValidationState.Unverified, synchronized.ValidationState);
        Assert.Null(synchronized.LastVerifiedAtUtc);
        Assert.Null(synchronized.ConsentedInputMode);
        Assert.Null(synchronized.ConsentedDisclosureVersion);
        Assert.Null(synchronized.ConsentGrantedAtUtc);
        Assert.Equal(AnalysisExecutionBackend.Local, execution.Settings.Backend);
        Assert.Equal(preset.ProfileId, execution.Settings.RemoteApiProfileId);
    }

    [Fact]
    public async Task StartupSync_UpgradesCustomV2ContractWithoutOverwritingUserSettings()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);
        var existing = await profiles.GetProfileAsync("custom-interface");
        Assert.NotNull(existing);
        var customized = await profiles.SaveProfileAsync(existing with
        {
            DisplayName = "User custom provider",
            EndpointId = "custom.chat-completions.user-endpoint",
            BaseUri = new Uri("https://user-api.example.test/v9/chat/completions"),
            ModelId = "user-model-v9",
            PromptVersion = "picforlater.remote-analysis.v2",
            OutputSchemaVersion = "picforlater.analysis.legacy.v0",
            MaxTextChars = 23_456,
            MaxImageBytes = 5_432_100,
            MaxOutputTokens = 2_345,
            TimeoutSeconds = 234,
            PrivacyUrl = new Uri("https://user-api.example.test/privacy"),
            TermsUrl = new Uri("https://user-api.example.test/terms"),
            RetentionTrainingStatement = "Synthetic user-defined retention policy.",
            CredentialReference = "user-owned-credential-reference",
            DisclosureVersion = "user-custom.disclosure.v7",
            AuthenticationKind = RemoteApiAuthenticationKind.XApiKey,
            StructuredOutputMode = RemoteStructuredOutputMode.JsonObject,
            EndpointTrustMode = RemoteEndpointTrustMode.PublicHttps,
            ApiVersion = "2026-08-31",
            DisableProviderFallbacks = true,
            DisableExternalSearch = true,
            ReasoningMode = RemoteReasoningMode.High,
            ReasoningWireFormat = RemoteReasoningWireFormat.ReasoningEffort,
        });
        var verifiedAt = new DateTimeOffset(2026, 8, 31, 1, 0, 0, TimeSpan.Zero);
        await ExecuteAsync(
            root.Paths.DatabasePath,
            """
            UPDATE RemoteApiProfiles
            SET ValidationState = @valid,
                LastVerifiedAtUtc = @verifiedAt,
                ConsentedInputMode = @inputMode,
                ConsentedDisclosureVersion = DisclosureVersion,
                ConsentGrantedAtUtc = @verifiedAt
            WHERE ProfileId = @profileId;

            UPDATE AnalysisSettings
            SET ExecutionBackend = @remote,
                RemoteInputMode = @inputMode,
                RemoteApiProfileId = @profileId
            WHERE Id = 1;
            """,
            ("@valid", (int)RemoteApiProfileValidationState.Valid),
            ("@verifiedAt", verifiedAt.ToString("O")),
            ("@inputMode", (int)RemoteInputMode.LocalOcrText),
            ("@profileId", customized.ProfileId),
            ("@remote", (int)AnalysisExecutionBackend.RemoteApi));

        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);

        var synchronized = await profiles.GetProfileAsync(customized.ProfileId);
        var execution = await profiles.GetExecutionStateAsync();
        Assert.NotNull(synchronized);
        Assert.Equal("picforlater.remote-analysis.v3", synchronized.PromptVersion);
        Assert.Equal(QwenStructuredOutputParser.SchemaVersion, synchronized.OutputSchemaVersion);
        Assert.Equal(customized.DisplayName, synchronized.DisplayName);
        Assert.Equal(customized.EndpointId, synchronized.EndpointId);
        Assert.Equal(customized.BaseUri, synchronized.BaseUri);
        Assert.Equal(customized.ModelId, synchronized.ModelId);
        Assert.Equal(customized.SupportedInputModes, synchronized.SupportedInputModes);
        Assert.Equal(customized.MaxTextChars, synchronized.MaxTextChars);
        Assert.Equal(customized.MaxImageBytes, synchronized.MaxImageBytes);
        Assert.Equal(customized.MaxOutputTokens, synchronized.MaxOutputTokens);
        Assert.Equal(customized.TimeoutSeconds, synchronized.TimeoutSeconds);
        Assert.Equal(customized.PrivacyUrl, synchronized.PrivacyUrl);
        Assert.Equal(customized.TermsUrl, synchronized.TermsUrl);
        Assert.Equal(customized.RetentionTrainingStatement, synchronized.RetentionTrainingStatement);
        Assert.Equal(customized.CredentialReference, synchronized.CredentialReference);
        Assert.Equal(customized.DisclosureVersion, synchronized.DisclosureVersion);
        Assert.Equal(customized.Protocol, synchronized.Protocol);
        Assert.Equal(customized.AuthenticationKind, synchronized.AuthenticationKind);
        Assert.Equal(customized.StructuredOutputMode, synchronized.StructuredOutputMode);
        Assert.Equal(customized.EndpointTrustMode, synchronized.EndpointTrustMode);
        Assert.Equal(customized.ApiVersion, synchronized.ApiVersion);
        Assert.Equal(customized.DisableProviderFallbacks, synchronized.DisableProviderFallbacks);
        Assert.Equal(customized.DisableExternalSearch, synchronized.DisableExternalSearch);
        Assert.Equal(customized.ReasoningMode, synchronized.ReasoningMode);
        Assert.Equal(customized.ReasoningWireFormat, synchronized.ReasoningWireFormat);
        Assert.Equal(RemoteApiProfileValidationState.Unverified, synchronized.ValidationState);
        Assert.Null(synchronized.LastVerifiedAtUtc);
        Assert.Null(synchronized.ConsentedInputMode);
        Assert.Null(synchronized.ConsentedDisclosureVersion);
        Assert.Null(synchronized.ConsentGrantedAtUtc);
        Assert.Equal(AnalysisExecutionBackend.Local, execution.Settings.Backend);
        Assert.Equal(customized.ProfileId, execution.Settings.RemoteApiProfileId);
    }

    [Fact]
    public async Task StartupSync_DoesNotResaveCurrentV3ProfilesOrInvalidateTrust()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);
        var existing = await profiles.GetProfileAsync("custom-interface");
        Assert.NotNull(existing);
        var verifiedAt = new DateTimeOffset(2026, 8, 31, 2, 0, 0, TimeSpan.Zero);
        await ExecuteAsync(
            root.Paths.DatabasePath,
            """
            UPDATE RemoteApiProfiles
            SET ValidationState = @valid,
                LastVerifiedAtUtc = @verifiedAt,
                ConsentedInputMode = @inputMode,
                ConsentedDisclosureVersion = DisclosureVersion,
                ConsentGrantedAtUtc = @verifiedAt
            WHERE ProfileId = @profileId;
            """,
            ("@valid", (int)RemoteApiProfileValidationState.Valid),
            ("@verifiedAt", verifiedAt.ToString("O")),
            ("@inputMode", (int)RemoteInputMode.LocalOcrText),
            ("@profileId", existing.ProfileId));
        var countingProfiles = new CountingRemoteApiProfileService(profiles);

        await RemoteApiProviderCatalog.EnsureProfilesAsync(countingProfiles);

        Assert.Equal(0, countingProfiles.SaveProfileCallCount);
        var synchronized = await profiles.GetProfileAsync(existing.ProfileId);
        Assert.NotNull(synchronized);
        Assert.Equal(RemoteApiProfileValidationState.Valid, synchronized.ValidationState);
        Assert.Equal(verifiedAt, synchronized.LastVerifiedAtUtc);
        Assert.Equal(RemoteInputMode.LocalOcrText, synchronized.ConsentedInputMode);
        Assert.Equal(verifiedAt, synchronized.ConsentGrantedAtUtc);
    }

    [Theory]
    [InlineData("openai-official")]
    [InlineData("custom-interface")]
    public async Task StartupSync_PreservesUserEndpoint(string profileId)
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);
        var preset = RemoteApiProviderCatalog.GetPreset(profileId);
        var existing = await profiles.GetProfileAsync(profileId);
        Assert.NotNull(existing);
        var userEndpoint = new Uri("https://user-endpoint.example.test/v1/chat/completions");

        await profiles.SaveProfileAsync(existing with
        {
            EndpointId = RemoteApiProviderCatalog.GetEndpointId(preset, userEndpoint),
            BaseUri = userEndpoint,
            EndpointTrustMode = RemoteEndpointTrustMode.PublicHttps,
        });

        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);

        var synchronized = await profiles.GetProfileAsync(profileId);
        Assert.NotNull(synchronized);
        Assert.Equal(userEndpoint, synchronized.BaseUri);
        Assert.EndsWith(".user-endpoint", synchronized.EndpointId, StringComparison.Ordinal);
        Assert.Equal(RemoteEndpointTrustMode.PublicHttps, synchronized.EndpointTrustMode);
    }

    private static async Task ExecuteAsync(
        string databasePath,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed class CountingRemoteApiProfileService(IRemoteApiProfileService inner)
        : IRemoteApiProfileService
    {
        public int GetProfilesCallCount { get; private set; }

        public int GetProfileCallCount { get; private set; }

        public int SaveProfileCallCount { get; private set; }

        public Task<RemoteAnalysisExecutionState> GetExecutionStateAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetExecutionStateAsync(cancellationToken);

        public Task<IReadOnlyList<RemoteApiProfile>> GetProfilesAsync(
            CancellationToken cancellationToken = default)
        {
            GetProfilesCallCount++;
            return inner.GetProfilesAsync(cancellationToken);
        }

        public Task<RemoteApiProfile?> GetProfileAsync(
            string profileId,
            CancellationToken cancellationToken = default)
        {
            GetProfileCallCount++;
            return inner.GetProfileAsync(profileId, cancellationToken);
        }

        public Task<RemoteApiProfile> SaveProfileAsync(
            RemoteApiProfile profile,
            CancellationToken cancellationToken = default)
        {
            SaveProfileCallCount++;
            return inner.SaveProfileAsync(profile, cancellationToken);
        }

        public Task DeleteProfileAsync(
            string profileId,
            CancellationToken cancellationToken = default) =>
            inner.DeleteProfileAsync(profileId, cancellationToken);

        public Task SetOutputLanguageAsync(
            AnalysisOutputLanguage outputLanguage,
            CancellationToken cancellationToken = default) =>
            inner.SetOutputLanguageAsync(outputLanguage, cancellationToken);

        public Task SelectLocalAsync(CancellationToken cancellationToken = default) =>
            inner.SelectLocalAsync(cancellationToken);

        public Task SelectRemoteAsync(
            string profileId,
            RemoteInputMode inputMode,
            CancellationToken cancellationToken = default) =>
            inner.SelectRemoteAsync(profileId, inputMode, cancellationToken);
    }
}
