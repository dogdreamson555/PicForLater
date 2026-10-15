using PicForLater.Analysis;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class RemoteApiProviderCatalogTests
{
    [Theory]
    [InlineData("openai-official", "gpt-4.1-mini-2025-04-14", "gpt-6-luna", true)]
    [InlineData("anthropic-official", "claude-sonnet-4-5-20250929", "claude-sonnet-4-6", true)]
    [InlineData("google-gemini-official", "gemini-3.5-flash", "gemini-3.6-flash", true)]
    [InlineData("kimi-official", "kimi-k2.5", "kimi-k2.6", true)]
    [InlineData("volcengine-doubao-official", "doubao-seed-2-0-lite-260215", "doubao-seed-2-1-lite-260915", true)]
    [InlineData("deepseek-official", "deepseek-v4-flash", "deepseek-flash", true)]
    [InlineData("deepseek-official", "deepseek-v4-flash-vision-exp", "deepseek-flash", true)]
    [InlineData("tencent-hunyuan-official", "hy3-preview", "hy3", false)]
    [InlineData("siliconflow-official", "Pro/zai-org/GLM-4.7", "Pro/zai-org/GLM-5.1", false)]
    [InlineData("groq-official", "meta-llama/llama-4-scout-17b-16e-instruct", "qwen/qwen3.8-27b", true)]
    [InlineData("baidu-qianfan-official", "ernie-4.5-turbo-128k", "ernie-5.0", true)]
    [InlineData("minimax-official", "MiniMax-M2.7", "MiniMax-M3", true)]
    [InlineData("perplexity-sonar-official", "sonar", "sonar", true)]
    public async Task StartupSync_UpdatesLegacyPresetsAndInvalidatesTrust(
        string profileId, string oldModel, string newModel, bool supportsImage)
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);
        await ExecuteAsync(
            root.Paths.DatabasePath,
            """
            UPDATE RemoteApiProfiles
            SET ModelId = @model,
                SupportedInputModesJson = @modes,
                ReasoningMode = @reasoningMode,
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
            ("@model", oldModel),
            ("@modes", """["localOcrText"]"""),
            ("@reasoningMode", (int)RemoteReasoningMode.ProviderDefault),
            ("@valid", (int)RemoteApiProfileValidationState.Valid),
            ("@verifiedAt", "2026-08-01T00:00:00.0000000+00:00"),
            ("@inputMode", (int)RemoteInputMode.LocalOcrText),
            ("@profileId", profileId),
            ("@remote", (int)AnalysisExecutionBackend.RemoteApi));

        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);

        var updated = await profiles.GetProfileAsync(profileId);
        Assert.NotNull(updated);
        Assert.Equal(newModel, updated.ModelId);
        Assert.Equal(RemoteApiProviderCatalog.GetPreset(profileId).ReasoningMode, updated.ReasoningMode);
        Assert.Equal(supportsImage, updated.SupportedInputModes.Contains(RemoteInputMode.DirectImage));
        Assert.Equal(RemoteApiProfileValidationState.Unverified, updated.ValidationState);
        Assert.Null(updated.LastVerifiedAtUtc);
        Assert.Null(updated.ConsentedInputMode);
        Assert.Null(updated.ConsentedDisclosureVersion);
        Assert.Null(updated.ConsentGrantedAtUtc);
        Assert.Equal(AnalysisExecutionBackend.Local, (await profiles.GetExecutionStateAsync()).Settings.Backend);
        if (supportsImage)
        {
            var error = await Assert.ThrowsAsync<RemoteApiProfileException>(() =>
                profiles.SelectRemoteAsync(profileId, RemoteInputMode.DirectImage));
            Assert.Equal("remote.profile-not-verified", error.ErrorCode);
        }

        var counting = new CountingRemoteApiProfileService(profiles);
        await RemoteApiProviderCatalog.EnsureProfilesAsync(counting);
        Assert.Equal(0, counting.SaveProfileCallCount);
    }

    [Theory]
    [InlineData("openai-official", "user-selected-model")]
    [InlineData("openai-official", "gpt-4.1-mini")]
    [InlineData("anthropic-official", "user-selected-model")]
    [InlineData("anthropic-official", "claude-sonnet-4-5")]
    [InlineData("google-gemini-official", "user-selected-model")]
    [InlineData("kimi-official", "user-selected-model")]
    [InlineData("volcengine-doubao-official", "user-selected-model")]
    [InlineData("deepseek-official", "user-selected-model")]
    [InlineData("tencent-hunyuan-official", "user-selected-model")]
    [InlineData("siliconflow-official", "user-selected-model")]
    [InlineData("groq-official", "user-selected-model")]
    [InlineData("baidu-qianfan-official", "user-selected-model")]
    [InlineData("minimax-official", "user-selected-model")]
    [InlineData("perplexity-sonar-official", "user-selected-model")]
    [InlineData("meta-ai-official", "user-selected-model")]
    [InlineData("xiaomi-mimo-official", "user-selected-model")]
    public async Task StartupSync_PreservesUserModelOnUpdatedPresets(string profileId, string modelId)
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);
        var existing = await profiles.GetProfileAsync(profileId);
        Assert.NotNull(existing);
        await profiles.SaveProfileAsync(existing with
        {
            ModelId = modelId,
            ReasoningMode = RemoteReasoningMode.ProviderDefault,
            MaxOutputTokens = 2_345,
            TimeoutSeconds = 234,
        });

        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);

        var synchronized = await profiles.GetProfileAsync(profileId);
        Assert.NotNull(synchronized);
        Assert.Equal(modelId, synchronized.ModelId);
        Assert.Equal(RemoteReasoningMode.ProviderDefault, synchronized.ReasoningMode);
        Assert.Equal(2_345, synchronized.MaxOutputTokens);
        Assert.Equal(234, synchronized.TimeoutSeconds);
    }

    [Theory]
    [InlineData("openai-official", "gpt-4.1-mini-2025-04-14", RemoteReasoningMode.High)]
    [InlineData("deepseek-official", "deepseek-v4-flash", RemoteReasoningMode.Disabled)]
    public async Task StartupSync_PreservesExplicitReasoningAndLimitsWhenMigratingDefaultModel(
        string profileId, string oldModel, RemoteReasoningMode reasoningMode)
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);
        var existing = await profiles.GetProfileAsync(profileId);
        Assert.NotNull(existing);
        await profiles.SaveProfileAsync(existing with
        {
            ModelId = oldModel,
            ReasoningMode = reasoningMode,
            MaxOutputTokens = 2_345,
            TimeoutSeconds = 234,
        });

        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);

        var synchronized = await profiles.GetProfileAsync(profileId);
        Assert.NotNull(synchronized);
        Assert.Equal(RemoteApiProviderCatalog.GetPreset(profileId).DefaultModelId, synchronized.ModelId);
        Assert.Equal(reasoningMode, synchronized.ReasoningMode);
        Assert.Equal(2_345, synchronized.MaxOutputTokens);
        Assert.Equal(234, synchronized.TimeoutSeconds);
    }

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
    [InlineData("openai-official", null)]
    [InlineData("openai-official", "gpt-4.1-mini-2025-04-14")]
    [InlineData("kimi-official", "kimi-k2.5")]
    [InlineData("custom-interface", null)]
    [InlineData("meta-ai-official", null)]
    [InlineData("xiaomi-mimo-official", null)]
    public async Task StartupSync_PreservesUserEndpoint(string profileId, string? modelId)
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
            ModelId = modelId ?? existing.ModelId,
            ReasoningMode = RemoteReasoningMode.ProviderDefault,
            EndpointTrustMode = RemoteEndpointTrustMode.PublicHttps,
        });

        await RemoteApiProviderCatalog.EnsureProfilesAsync(profiles);

        var synchronized = await profiles.GetProfileAsync(profileId);
        Assert.NotNull(synchronized);
        Assert.Equal(userEndpoint, synchronized.BaseUri);
        Assert.Equal(modelId ?? existing.ModelId, synchronized.ModelId);
        Assert.Equal(RemoteReasoningMode.ProviderDefault, synchronized.ReasoningMode);
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
