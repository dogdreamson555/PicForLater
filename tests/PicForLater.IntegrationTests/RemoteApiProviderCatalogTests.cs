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
    public async Task StartupSync_UpgradesSelectedLegacyPresetAndReturnsExecutionToLocal()
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
        Assert.Equal(RemoteApiProfileValidationState.Unverified, synchronized.ValidationState);
        Assert.Null(synchronized.LastVerifiedAtUtc);
        Assert.Null(synchronized.ConsentedInputMode);
        Assert.Null(synchronized.ConsentedDisclosureVersion);
        Assert.Null(synchronized.ConsentGrantedAtUtc);
        Assert.Equal(AnalysisExecutionBackend.Local, execution.Settings.Backend);
        Assert.Equal(preset.ProfileId, execution.Settings.RemoteApiProfileId);
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
            CancellationToken cancellationToken = default) =>
            inner.SaveProfileAsync(profile, cancellationToken);

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
