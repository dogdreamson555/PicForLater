using System.Text.Json;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class RemoteAnalysisProfileTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task NewAndUpgradedSettings_DefaultToLocalExecution()
    {
        using var root = new TemporaryAppDataRoot();
        var v7Initializer = new SqliteDatabaseInitializer(
            root.Paths,
            SqliteSchema.Migrations.Take(7).ToArray());
        await v7Initializer.InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor());
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "legacy-profile.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        const string legacySnapshotJson =
            """
            {
              "analysisMode": 2,
              "revision": 1,
              "slots": []
            }
            """;
        await ExecuteAsync(
            root.Paths.DatabasePath,
            """
            UPDATE AnalysisJobs
            SET ModelProfileSnapshotJson = @snapshot
            WHERE ImageItemId = @itemId;
            """,
            ("@snapshot", legacySnapshotJson),
            ("@itemId", imported.ImageItemId.ToString("D")));

        var upgraded = await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();

        Assert.Equal(7, upgraded.PreviousVersion);
        Assert.Equal(12, upgraded.CurrentVersion);
        Assert.NotNull(upgraded.BackupFilePath);
        using var remoteProfiles = new SqliteRemoteApiProfileService(root.Paths);
        var execution = await remoteProfiles.GetExecutionStateAsync();
        Assert.Equal(AnalysisExecutionBackend.Local, execution.Settings.Backend);
        Assert.Null(execution.Settings.RemoteInputMode);
        Assert.Null(execution.Settings.RemoteApiProfileId);
        Assert.Null(execution.Profile);

        var lease = (await new SqliteAnalysisJobStore(root.Paths).TryLeaseNextAsync(
            "legacy-profile-reader",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(1),
            maximumAttempts: 3)).Lease;
        Assert.NotNull(lease);
        Assert.Equal(AnalysisExecutionBackend.Local, lease.ProfileSnapshot.ExecutionBackend);
        Assert.Null(lease.ProfileSnapshot.RemoteInputMode);
        Assert.Null(lease.ProfileSnapshot.RemoteApiProfile);
    }

    [Fact]
    public async Task CombinedProvider_PinsRemoteProfileAndSelectionOnlyForNewJobs()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var localProfiles = new SqliteModelPackageService(root.Paths, new NeverUsedModelValidator());
        using var remoteProfiles = new SqliteRemoteApiProfileService(root.Paths);
        var profile = await remoteProfiles.SaveProfileAsync(CreateValidProfile());
        var combined = new CombinedAnalysisProfileSnapshotProvider(localProfiles, remoteProfiles);
        var beforeSelection = await combined.GetCurrentSnapshotAsync();
        Assert.Equal(AnalysisExecutionBackend.Local, beforeSelection.ExecutionBackend);
        Assert.Equal(1, beforeSelection.Revision);

        await remoteProfiles.SelectRemoteAsync(
            profile.ProfileId,
            RemoteInputMode.LocalOcrText);

        var remoteSnapshot = await combined.GetCurrentSnapshotAsync();
        Assert.Equal(AnalysisExecutionBackend.RemoteApi, remoteSnapshot.ExecutionBackend);
        Assert.Equal(RemoteInputMode.LocalOcrText, remoteSnapshot.RemoteInputMode);
        Assert.Equal(2, remoteSnapshot.Revision);
        Assert.NotNull(remoteSnapshot.RemoteApiProfile);
        Assert.Equal(profile.ProfileId, remoteSnapshot.RemoteApiProfile.ProfileId);
        Assert.Equal(profile.ProviderId, remoteSnapshot.RemoteApiProfile.ProviderId);
        Assert.Equal(profile.BaseUri, remoteSnapshot.RemoteApiProfile.BaseUri);
        Assert.Equal(profile.ModelId, remoteSnapshot.RemoteApiProfile.ModelId);
        Assert.Equal(profile.PromptVersion, remoteSnapshot.RemoteApiProfile.PromptVersion);
        Assert.Equal(profile.OutputSchemaVersion, remoteSnapshot.RemoteApiProfile.OutputSchemaVersion);
        Assert.Equal(profile.CredentialReference, remoteSnapshot.RemoteApiProfile.CredentialReference);
        Assert.Equal(profile.DisclosureVersion, remoteSnapshot.RemoteApiProfile.ConsentVersion);
        Assert.Equal(profile.Protocol, remoteSnapshot.RemoteApiProfile.Protocol);
        Assert.Equal(profile.AuthenticationKind, remoteSnapshot.RemoteApiProfile.AuthenticationKind);
        Assert.Equal(profile.StructuredOutputMode, remoteSnapshot.RemoteApiProfile.StructuredOutputMode);
        Assert.Equal(profile.EndpointTrustMode, remoteSnapshot.RemoteApiProfile.EndpointTrustMode);
        Assert.Equal(profile.ApiVersion, remoteSnapshot.RemoteApiProfile.ApiVersion);
        Assert.Equal(profile.DisableProviderFallbacks, remoteSnapshot.RemoteApiProfile.DisableProviderFallbacks);
        Assert.Equal(profile.DisableExternalSearch, remoteSnapshot.RemoteApiProfile.DisableExternalSearch);
        Assert.Equal(profile.ReasoningMode, remoteSnapshot.RemoteApiProfile.ReasoningMode);
        Assert.Equal(
            ModelProfileSnapshot.Default.GetSlot(ModelCapability.Ocr),
            remoteSnapshot.GetSlot(ModelCapability.Ocr));

        var serializedSnapshot = JsonSerializer.Serialize(
            remoteSnapshot,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(profile.CredentialReference, serializedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("\"secret\"", serializedSnapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"apiKey\"", serializedSnapshot, StringComparison.OrdinalIgnoreCase);

        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: combined);
        var remoteImport = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "remote-job.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);

        await remoteProfiles.SelectLocalAsync();
        var localImport = await importer.ImportAsync(
            new MemoryStream([.. TinyPng, 0], writable: false),
            "local-job.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);

        var remoteJobSnapshot = await ReadJobSnapshotAsync(
            root.Paths.DatabasePath,
            remoteImport.ImageItemId);
        var localJobSnapshot = await ReadJobSnapshotAsync(
            root.Paths.DatabasePath,
            localImport.ImageItemId);
        Assert.Equal(AnalysisExecutionBackend.RemoteApi, remoteJobSnapshot.ExecutionBackend);
        Assert.Equal(RemoteInputMode.LocalOcrText, remoteJobSnapshot.RemoteInputMode);
        Assert.Equal(2, remoteJobSnapshot.Revision);
        Assert.Equal(AnalysisExecutionBackend.Local, localJobSnapshot.ExecutionBackend);
        Assert.Null(localJobSnapshot.RemoteInputMode);
        Assert.Null(localJobSnapshot.RemoteApiProfile);
        Assert.Equal(3, localJobSnapshot.Revision);
    }

    [Fact]
    public async Task SwitchingToLocal_PreservesLastRemoteSelectionForNextSwitch()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var remoteProfiles = new SqliteRemoteApiProfileService(root.Paths);
        var saved = await remoteProfiles.SaveProfileAsync(CreateValidProfile());

        await remoteProfiles.SelectRemoteAsync(saved.ProfileId, RemoteInputMode.LocalOcrText);
        await remoteProfiles.SelectLocalAsync();

        var localState = await remoteProfiles.GetExecutionStateAsync();
        Assert.Equal(AnalysisExecutionBackend.Local, localState.Settings.Backend);
        Assert.Equal(RemoteInputMode.LocalOcrText, localState.Settings.RemoteInputMode);
        Assert.Equal(saved.ProfileId, localState.Settings.RemoteApiProfileId);
        Assert.NotNull(localState.Profile);
        Assert.Equal(saved.ProfileId, localState.Profile.ProfileId);

        var localProfiles = new SqliteModelPackageService(root.Paths, new NeverUsedModelValidator());
        var localSnapshot = await new CombinedAnalysisProfileSnapshotProvider(
                localProfiles,
                remoteProfiles)
            .GetCurrentSnapshotAsync();
        Assert.Equal(AnalysisExecutionBackend.Local, localSnapshot.ExecutionBackend);
        Assert.Null(localSnapshot.RemoteInputMode);
        Assert.Null(localSnapshot.RemoteApiProfile);

        await remoteProfiles.SelectRemoteAsync(
            localState.Settings.RemoteApiProfileId!,
            localState.Settings.RemoteInputMode!.Value);
        var restoredState = await remoteProfiles.GetExecutionStateAsync();
        Assert.Equal(AnalysisExecutionBackend.RemoteApi, restoredState.Settings.Backend);
        Assert.Equal(saved.ProfileId, restoredState.Settings.RemoteApiProfileId);
        Assert.Equal(RemoteInputMode.LocalOcrText, restoredState.Settings.RemoteInputMode);
    }

    [Fact]
    public async Task ScopeChange_InvalidatesConsentAndPreventsRemoteSelection()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        var saved = await profiles.SaveProfileAsync(CreateValidProfile());
        var modeChangeException = await Assert.ThrowsAsync<RemoteApiProfileException>(() =>
            profiles.SelectRemoteAsync(saved.ProfileId, RemoteInputMode.DirectImage));
        Assert.Equal("remote.consent-required", modeChangeException.ErrorCode);

        var changed = await profiles.SaveProfileAsync(saved with
        {
            BaseUri = new Uri("https://changed.example.test/v1/"),
        });

        Assert.Null(changed.ConsentedInputMode);
        Assert.Null(changed.ConsentedDisclosureVersion);
        Assert.Null(changed.ConsentGrantedAtUtc);
        var exception = await Assert.ThrowsAsync<RemoteApiProfileException>(() =>
            profiles.SelectRemoteAsync(changed.ProfileId, RemoteInputMode.LocalOcrText));
        Assert.Equal("remote.consent-required", exception.ErrorCode);
    }

    [Fact]
    public async Task ExplicitReasoningMode_IsPersistedForOpenAiCompatibleProfile()
    {
        using var temporaryRoot = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(temporaryRoot.Paths).InitializeAsync();
        var service = new SqliteRemoteApiProfileService(temporaryRoot.Paths);
        var profile = CreateValidProfile() with
        {
            Protocol = RemoteApiProtocol.OpenAiChatCompletions,
            AuthenticationKind = RemoteApiAuthenticationKind.Bearer,
            ApiVersion = null,
            ReasoningMode = RemoteReasoningMode.Disabled,
            ReasoningWireFormat = RemoteReasoningWireFormat.ThinkingObject,
        };

        await service.SaveProfileAsync(profile);

        var persisted = await service.GetProfileAsync(profile.ProfileId);
        Assert.NotNull(persisted);
        Assert.Equal(RemoteReasoningMode.Disabled, persisted.ReasoningMode);
        Assert.Equal(RemoteReasoningWireFormat.ThinkingObject, persisted.ReasoningWireFormat);
    }

    [Fact]
    public async Task SelectedProfile_CannotBeDisabledOrDeletedWithoutReturningLocal()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        using var profiles = new SqliteRemoteApiProfileService(root.Paths);
        var saved = await profiles.SaveProfileAsync(CreateValidProfile());
        await profiles.SelectRemoteAsync(saved.ProfileId, RemoteInputMode.LocalOcrText);

        var disableException = await Assert.ThrowsAsync<RemoteApiProfileException>(() =>
            profiles.SaveProfileAsync(saved with { IsEnabled = false }));
        Assert.Equal("remote.active-profile-change-requires-local", disableException.ErrorCode);
        var deleteException = await Assert.ThrowsAsync<RemoteApiProfileException>(() =>
            profiles.DeleteProfileAsync(saved.ProfileId));
        Assert.Equal("remote.selected-profile-cannot-be-deleted", deleteException.ErrorCode);

        await profiles.SelectLocalAsync();
        var disabled = await profiles.SaveProfileAsync(saved with { IsEnabled = false });
        Assert.False(disabled.IsEnabled);
        await profiles.DeleteProfileAsync(saved.ProfileId);
        Assert.Null(await profiles.GetProfileAsync(saved.ProfileId));
    }

    [Fact]
    public async Task RequestAuthorizer_RechecksCurrentConsentAndProfileBeforeImageSend()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var localProfiles = new SqliteModelPackageService(
            root.Paths,
            new NeverUsedModelValidator());
        using var remoteProfiles = new SqliteRemoteApiProfileService(root.Paths);
        var saved = await remoteProfiles.SaveProfileAsync(CreateValidProfile() with
        {
            ConsentedInputMode = RemoteInputMode.DirectImage,
        });
        await remoteProfiles.SelectRemoteAsync(
            saved.ProfileId,
            RemoteInputMode.DirectImage);
        var snapshot = await new CombinedAnalysisProfileSnapshotProvider(
                localProfiles,
                remoteProfiles)
            .GetCurrentSnapshotAsync();
        var remoteSnapshot = Assert.IsType<RemoteApiProfileSnapshot>(
            snapshot.RemoteApiProfile);
        var authorizer = new RemoteApiRequestAuthorizer(remoteProfiles);

        await authorizer.EnsureAuthorizedAsync(
            remoteSnapshot,
            RemoteInputMode.DirectImage);

        await remoteProfiles.SelectLocalAsync();
        await remoteProfiles.SaveProfileAsync(saved with { IsEnabled = false });
        var exception = await Assert.ThrowsAsync<RemoteAnalysisProviderException>(
            () => authorizer.EnsureAuthorizedAsync(
                remoteSnapshot,
                RemoteInputMode.DirectImage));
        Assert.Equal("remote.profile-disabled", exception.ErrorCode);
        Assert.False(exception.IsRetryable);
    }

    private static RemoteApiProfile CreateValidProfile() => new()
    {
        ProfileId = "openai-compatible-primary",
        ProviderId = "provider.openai-compatible",
        DisplayName = "OpenAI-compatible test profile",
        EndpointId = "fixed.test.v1",
        BaseUri = new Uri("https://api.example.test/v1/"),
        ModelId = "vision-model-1",
        SupportedInputModes =
        [
            RemoteInputMode.LocalOcrText,
            RemoteInputMode.DirectImage,
        ],
        PromptVersion = "remote-analysis.prompt.v1",
        OutputSchemaVersion = "picforlater.analysis.v1",
        MaxTextChars = 64_000,
        MaxImageBytes = 8 * 1024 * 1024,
        MaxOutputTokens = 1_024,
        TimeoutSeconds = 60,
        PrivacyUrl = new Uri("https://example.test/privacy"),
        TermsUrl = new Uri("https://example.test/terms"),
        RetentionTrainingStatement = "Synthetic test policy statement.",
        RetentionTrainingVerifiedAtUtc = new DateTimeOffset(
            2026,
            7,
            31,
            0,
            0,
            0,
            TimeSpan.Zero),
        CredentialReference = "credential-ref-1",
        DisclosureVersion = "remote-disclosure.v1",
        Protocol = RemoteApiProtocol.AnthropicMessages,
        AuthenticationKind = RemoteApiAuthenticationKind.XApiKey,
        StructuredOutputMode = RemoteStructuredOutputMode.JsonSchema,
        EndpointTrustMode = RemoteEndpointTrustMode.FixedHttps,
        ApiVersion = "2023-06-01",
        DisableProviderFallbacks = true,
        DisableExternalSearch = true,
        IsEnabled = true,
        ValidationState = RemoteApiProfileValidationState.Valid,
        LastVerifiedAtUtc = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero),
        ConsentedInputMode = RemoteInputMode.LocalOcrText,
        ConsentedDisclosureVersion = "remote-disclosure.v1",
        ConsentGrantedAtUtc = new DateTimeOffset(2026, 7, 31, 0, 1, 0, TimeSpan.Zero),
    };

    private static async Task<ModelProfileSnapshot> ReadJobSnapshotAsync(
        string databasePath,
        Guid imageItemId)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ModelProfileSnapshotJson
            FROM AnalysisJobs
            WHERE ImageItemId = @itemId;
            """;
        command.Parameters.AddWithValue("@itemId", imageItemId.ToString("D"));
        var json = Convert.ToString(await command.ExecuteScalarAsync())!;
        return JsonSerializer.Deserialize<ModelProfileSnapshot>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The saved job snapshot is empty.");
    }

    private static async Task ExecuteAsync(
        string databasePath,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed class NeverUsedModelValidator : IModelPackageValidator
    {
        public Task<ValidatedModelPackage> ValidateAsync(
            string packageDirectoryPath,
            bool runInferenceSelfTest,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Model validation is not used when reading a profile.");
    }

    private sealed class FakeImageProcessor : IImageContentProcessor
    {
        public Task<ImageInspection> InspectAndCreateThumbnailAsync(
            Stream source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImageInspection(
                ManagedImageFormat.Png,
                "image/png",
                1,
                1,
                TinyPng));
    }
}
