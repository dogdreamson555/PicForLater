using System.Text.Json;
using Microsoft.Data.Sqlite;
using PicForLater.Analysis;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class AnalysisWorkflowTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task Worker_PersistsStagesAndAppliesExtractiveDraft()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "event.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        using var signal = new AnalysisQueueWakeSignal();
        var ocrProvider = new FakeOcrProvider();
        var worker = new AnalysisWorker(
            "test-worker",
            new SqliteAnalysisJobStore(root.Paths),
            storage,
            ocrProvider,
            new ExtractiveTextComposer(),
            signal);

        Assert.True(await worker.ProcessNextAsync());

        var entry = await new LibraryService(root.Paths, storage).GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal(1, ocrProvider.CallCount);
        Assert.Equal("项目评审会议", entry.Item.Title);
        Assert.Equal("7月20日 14:30 会议室A", entry.Item.Summary);
        Assert.Equal(AnalysisState.Completed, entry.Item.AnalysisState);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisJobs WHERE State = 4 AND CurrentStage = 4;"));
        Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM AnalysisStageResults;"));
        Assert.Equal("fake.local-ocr", await ScalarStringAsync(
            connection,
            "SELECT ProviderId FROM AnalysisStageResults WHERE Stage = 1;"));
        Assert.Equal("项目评审会议\n7月20日 14:30 会议室A", await ScalarStringAsync(
            connection,
            "SELECT FactText FROM AnalysisStageResults WHERE Stage = 1;"));
        Assert.Equal("local.conditional-router", await ScalarStringAsync(
            connection,
            "SELECT ProviderId FROM AnalysisStageResults WHERE Stage = 3;"));
        Assert.Equal("local.deterministic-entities", await ScalarStringAsync(
            connection,
            "SELECT ProviderId FROM AnalysisStageResults WHERE Stage = 2;"));
        Assert.Equal(4L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ExecutionLocation = 0;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Stage = 1 AND OutputKind = 1;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Stage = 2 AND OutputKind = 2;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Stage = 3 AND OutputKind = 3;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Stage = 4 AND OutputKind = 5;"));
        Assert.True(await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE Source = 'Ocr';") >= 1);
    }

    [Fact]
    public async Task VisionFailure_PreservesOcrDraftAndCompletesJobWithWarning()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        var profile = new ModelProfileSnapshot(
            AnalysisMode.AlwaysEnhance,
            Revision: 3,
            [
                new ModelSlotSelection(ModelCapability.Ocr, "test.ocr", null),
                new ModelSlotSelection(ModelCapability.VisionCaption, "local.qwen3-vl", "qwen@test"),
                new ModelSlotSelection(ModelCapability.TextComposition, "local.qwen3-vl", "qwen@test"),
                new ModelSlotSelection(ModelCapability.EntityExtraction, "test.entities", null),
            ]);
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: new FixedProfileProvider(profile));
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "event.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        using var signal = new AnalysisQueueWakeSignal();
        var worker = new AnalysisWorker(
            "fallback-worker",
            new SqliteAnalysisJobStore(root.Paths),
            storage,
            new FakeOcrProvider(),
            new ExtractiveTextComposer(),
            signal,
            new FailingVisionProvider());

        Assert.True(await worker.ProcessNextAsync());

        var entry = await new LibraryService(root.Paths, storage).GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal("项目评审会议", entry.Item.Title);
        Assert.Equal("7月20日 14:30 会议室A", entry.Item.Summary);
        Assert.Equal(ContentFieldSource.Fallback, entry.Item.TitleSource);
        Assert.Equal(AnalysisState.Completed, entry.Item.AnalysisState);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisJobs WHERE State = 4 AND CurrentStage = 4;"));
        Assert.Contains(
            "enhancement-fallback:qwen.output-token-limit-exceeded",
            await ScalarStringAsync(
                connection,
                "SELECT WarningsJson FROM AnalysisStageResults WHERE Stage = 3;"));
    }

    [Theory]
    [InlineData(AnalysisMode.Balanced)]
    [InlineData(AnalysisMode.AlwaysEnhance)]
    public async Task EnhancedModes_RunOcrAndMergeBothEntitySourcesWithoutChangingExtractiveDraft(
        AnalysisMode analysisMode)
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        var profile = new ModelProfileSnapshot(
            analysisMode,
            Revision: 4,
            [
                new ModelSlotSelection(ModelCapability.Ocr, "test.ocr", null),
                new ModelSlotSelection(ModelCapability.VisionCaption, "local.qwen3-vl", "qwen@test"),
                new ModelSlotSelection(ModelCapability.TextComposition, "local.extractive-text", null),
                new ModelSlotSelection(ModelCapability.EntityExtraction, "local.deterministic-entities", null),
            ]);
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: new FixedProfileProvider(profile));
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "event.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        using var signal = new AnalysisQueueWakeSignal();
        var ocrProvider = new FakeOcrProvider();
        var visionProvider = new ModelEntityVisionProvider();
        var worker = new AnalysisWorker(
            "model-entity-worker",
            new SqliteAnalysisJobStore(root.Paths),
            storage,
            ocrProvider,
            new ExtractiveTextComposer(),
            signal,
            visionProvider);

        Assert.True(await worker.ProcessNextAsync());

        var entry = await new LibraryService(root.Paths, storage).GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal(1, ocrProvider.CallCount);
        Assert.Equal(1, visionProvider.CallCount);
        Assert.Equal("项目评审会议", entry.Item.Title);
        Assert.Equal("7月20日 14:30 会议室A", entry.Item.Summary);
        Assert.Equal(ContentFieldSource.Fallback, entry.Item.TitleSource);
        Assert.Equal(ContentFieldSource.Fallback, entry.Item.SummarySource);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.True(await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE Source = 'Ocr';") >= 1);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE Source = 'Model' AND Kind = 'DateTime';"));
    }

    [Fact]
    public async Task GeneratedDraftSemantics_DoNotDependOnProviderId()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        var profile = new ModelProfileSnapshot(
            AnalysisMode.AlwaysEnhance,
            Revision: 5,
            [
                new ModelSlotSelection(ModelCapability.Ocr, "test.ocr", null),
                new ModelSlotSelection(ModelCapability.VisionCaption, "opaque.generator", "generic-model@1"),
                new ModelSlotSelection(ModelCapability.TextComposition, "opaque.generator", "generic-model@1"),
                new ModelSlotSelection(ModelCapability.EntityExtraction, "test.entities", null),
            ]);
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: new FixedProfileProvider(profile));
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "generated.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var library = new LibraryService(root.Paths, storage);
        var category = await library.CreateCategoryAsync("模型分类");
        using var signal = new AnalysisQueueWakeSignal();
        var worker = new AnalysisWorker(
            "generic-generated-worker",
            new SqliteAnalysisJobStore(root.Paths),
            storage,
            new FakeOcrProvider(),
            new ExtractiveTextComposer(),
            signal,
            new GeneratedDraftVisionProvider(category.Id));

        Assert.True(await worker.ProcessNextAsync());

        var entry = await library.GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal("通用模型标题", entry.Item.Title);
        Assert.Equal("通用模型简介。", entry.Item.Summary);
        Assert.Equal(ContentFieldSource.ModelSuggested, entry.Item.TitleSource);
        Assert.Equal(ContentFieldSource.ModelSuggested, entry.Item.SummarySource);
        Assert.Equal(category.Id, Assert.Single(entry.Categories).Category.Id);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal("opaque.generator", await ScalarStringAsync(
            connection,
            "SELECT ProviderId FROM AnalysisStageResults WHERE Stage = 4;"));
        Assert.Equal(2L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE Stage IN (3, 4) AND OutputKind = 4;"));
    }

    [Fact]
    public async Task RemoteOcrTextSnapshot_ReusesLocalFactStagesAndRemoteCompletionPath()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        var profile = ModelProfileSnapshot.Default with
        {
            AnalysisMode = AnalysisMode.OcrOnly,
            Revision = 2,
            ExecutionBackend = AnalysisExecutionBackend.RemoteApi,
            RemoteInputMode = RemoteInputMode.LocalOcrText,
            RemoteApiProfile = new RemoteApiProfileSnapshot
            {
                ProfileId = "test-remote",
                ProviderId = "opaque.remote-provider",
                EndpointId = "fixed.test",
                BaseUri = new Uri("https://api.example.test/v1/"),
                ModelId = "remote-model",
                PromptVersion = "prompt.v1",
                OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
                MaxTextChars = 10_000,
                MaxImageBytes = 1_000_000,
                MaxOutputTokens = 1_000,
                TimeoutSeconds = 30,
                CredentialReference = "credential-ref",
                ConsentVersion = "consent.v1",
            },
        };
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: new FixedProfileProvider(profile));
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "remote-pending.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var ocrProvider = new FakeOcrProvider();
        var remoteProvider = new RemoteOcrTextVisionProvider();
        using var signal = new AnalysisQueueWakeSignal();
        var worker = new AnalysisWorker(
            "remote-ocr-text-worker",
            new SqliteAnalysisJobStore(root.Paths),
            storage,
            ocrProvider,
            new ExtractiveTextComposer(),
            signal,
            remoteOcrTextProvider: remoteProvider);

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(1, ocrProvider.CallCount);
        Assert.Equal(1, remoteProvider.CallCount);
        var entry = await new LibraryService(root.Paths, storage).GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal(AnalysisState.Completed, entry.Item.AnalysisState);
        Assert.Equal("远程会议草稿", entry.Item.Title);
        Assert.Equal(ContentFieldSource.ModelSuggested, entry.Item.TitleSource);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisJobs WHERE State = 4 AND CurrentStage = 4 AND ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "';"));
        Assert.Equal(4L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "';"));
        Assert.Equal(2L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D")
            + "' AND Stage IN (1, 2) AND ExecutionLocation = 0 AND RemoteInputMode IS NULL;"));
        Assert.Equal(2L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D")
            + "' AND Stage IN (3, 4) AND ExecutionLocation = 1 AND RemoteInputMode = 1;"));
        Assert.Equal("opaque.remote-provider", await ScalarStringAsync(
            connection,
            "SELECT ProviderId FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "' AND Stage = 3;"));
        Assert.True(await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "' AND Source = 'Ocr';") >= 1);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "' AND Source = 'Model';"));
    }

    [Fact]
    public async Task RemoteDirectImageSnapshot_SkipsLocalStagesAndUsesSharedCompletionPath()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        var profile = CreateRemoteVisionProfile();
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: new FixedProfileProvider(profile));
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "remote-vision-pending.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var ocrProvider = new ThrowingOcrProvider();
        var localVision = new NeverCalledVisionProvider();
        var entityExtractor = new NeverCalledEntityExtractor();
        var remoteVision = new RemoteDirectImageVisionProvider();
        using var signal = new AnalysisQueueWakeSignal();
        var worker = new AnalysisWorker(
            "remote-vision-worker",
            new SqliteAnalysisJobStore(root.Paths),
            storage,
            ocrProvider,
            new ExtractiveTextComposer(),
            signal,
            visionProvider: localVision,
            entityExtractor: entityExtractor,
            remoteVisionProvider: remoteVision);

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(0, ocrProvider.CallCount);
        Assert.Equal(0, localVision.CallCount);
        Assert.Equal(0, entityExtractor.CallCount);
        Assert.Equal(1, remoteVision.CallCount);
        var entry = await new LibraryService(root.Paths, storage).GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal(AnalysisState.Completed, entry.Item.AnalysisState);
        Assert.Equal("远程图片草稿", entry.Item.Title);
        Assert.Equal(ContentFieldSource.ModelSuggested, entry.Item.TitleSource);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisJobs WHERE State = 4 AND CurrentStage = 4 AND ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "';"));
        Assert.Equal(4L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "';"));
        Assert.Equal(2L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D")
            + "' AND Stage IN (1, 2) AND ExecutionLocation = 1"
            + " AND RemoteInputMode = 2 AND StageOutcome = 1 AND FactText = '';"));
        Assert.Equal(2L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D")
            + "' AND Stage IN (3, 4) AND ExecutionLocation = 1"
            + " AND RemoteInputMode = 2 AND StageOutcome = 0;"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D")
            + "' AND Source = 'Model' AND BoundingBoxJson IS NULL"
            + " AND AmbiguityReason = 'RemoteVisionNoLocalOcrEvidence';"));
        Assert.Equal(0L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM Reminders WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "';"));
    }

    [Fact]
    public async Task RemoteDirectImageFailure_DoesNotFallBackToLocalAnalysis()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: new FixedProfileProvider(
                CreateRemoteVisionProfile()));
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "remote-vision-failure.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var ocrProvider = new ThrowingOcrProvider();
        var localVision = new NeverCalledVisionProvider();
        var entityExtractor = new NeverCalledEntityExtractor();
        using var signal = new AnalysisQueueWakeSignal();
        var worker = new AnalysisWorker(
            "remote-vision-failure-worker",
            new SqliteAnalysisJobStore(root.Paths),
            storage,
            ocrProvider,
            new ExtractiveTextComposer(),
            signal,
            visionProvider: localVision,
            entityExtractor: entityExtractor,
            remoteVisionProvider: new FailingRemoteVisionProvider());

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(0, ocrProvider.CallCount);
        Assert.Equal(0, localVision.CallCount);
        Assert.Equal(0, entityExtractor.CallCount);
        var entry = await new LibraryService(root.Paths, storage).GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal(AnalysisState.NeedsAttention, entry.Item.AnalysisState);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisJobs WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D")
            + "' AND State = 5 AND CurrentStage = 3"
            + " AND LastErrorCode = 'remote.server-failure';"));
        Assert.Equal(3L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "';"));
        Assert.Equal(0L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "' AND Stage = 4;"));
    }

    [Fact]
    public async Task RemoteDirectImageLateCompletion_PreservesUserEditsByRevision()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: new FixedProfileProvider(
                CreateRemoteVisionProfile(revision: 4)));
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "remote-vision-revision.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var library = new LibraryService(root.Paths, storage);
        var remoteVision = new RemoteDirectImageVisionProvider(
            async () => await library.UpdateUserFieldsAsync(
                imported.ImageItemId,
                "人工标题",
                "人工简介"));
        using var signal = new AnalysisQueueWakeSignal();
        var worker = new AnalysisWorker(
            "remote-vision-revision-worker",
            new SqliteAnalysisJobStore(root.Paths),
            storage,
            new ThrowingOcrProvider(),
            new ExtractiveTextComposer(),
            signal,
            visionProvider: new NeverCalledVisionProvider(),
            entityExtractor: new NeverCalledEntityExtractor(),
            remoteVisionProvider: remoteVision);

        Assert.True(await worker.ProcessNextAsync());

        var entry = await library.GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal("人工标题", entry.Item.Title);
        Assert.Equal("人工简介", entry.Item.Summary);
        Assert.Equal(ContentFieldSource.User, entry.Item.TitleSource);
        Assert.Equal(ContentFieldSource.User, entry.Item.SummarySource);
        Assert.Equal(AnalysisState.Completed, entry.Item.AnalysisState);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(0L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "';"));
    }

    [Fact]
    public async Task RemoteOcrTextFailure_PreservesLocalDraftAndCandidatesWithoutCallingLocalVision()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        var profile = ModelProfileSnapshot.Default with
        {
            AnalysisMode = AnalysisMode.AlwaysEnhance,
            Revision = 4,
            ExecutionBackend = AnalysisExecutionBackend.RemoteApi,
            RemoteInputMode = RemoteInputMode.LocalOcrText,
            RemoteApiProfile = new RemoteApiProfileSnapshot
            {
                ProfileId = "failing-remote",
                ProviderId = "opaque.remote-provider",
                EndpointId = "fixed.test",
                BaseUri = new Uri("https://api.example.test/v1/"),
                ModelId = "remote-model",
                PromptVersion = "prompt.v1",
                OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
                MaxTextChars = 10_000,
                MaxImageBytes = 1_000_000,
                MaxOutputTokens = 1_000,
                TimeoutSeconds = 30,
                CredentialReference = "credential-ref",
                ConsentVersion = "consent.v1",
            },
        };
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: new FixedProfileProvider(profile));
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "remote-failure.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var localVision = new NeverCalledVisionProvider();
        using var signal = new AnalysisQueueWakeSignal();
        var worker = new AnalysisWorker(
            "remote-failure-worker",
            new SqliteAnalysisJobStore(root.Paths),
            storage,
            new FakeOcrProvider(),
            new ExtractiveTextComposer(),
            signal,
            visionProvider: localVision,
            remoteOcrTextProvider: new FailingRemoteOcrTextProvider());

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(0, localVision.CallCount);
        var entry = await new LibraryService(root.Paths, storage).GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal(AnalysisState.NeedsAttention, entry.Item.AnalysisState);
        Assert.Equal("项目评审会议", entry.Item.Title);
        Assert.Equal(ContentFieldSource.Fallback, entry.Item.TitleSource);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisJobs WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D")
            + "' AND State = 5 AND CurrentStage = 4 AND LastErrorCode = 'remote.server-failure';"));
        Assert.Equal(4L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "';"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM AnalysisStageResults WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D")
            + "' AND Stage = 3 AND ExecutionLocation = 1 AND RemoteInputMode = 1;"));
        Assert.Contains(
            "enhancement-fallback:remote.server-failure",
            await ScalarStringAsync(
                connection,
                "SELECT WarningsJson FROM AnalysisStageResults WHERE ImageItemId = '"
                + imported.ImageItemId.ToString("D") + "' AND Stage = 4;"));
        Assert.True(await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE ImageItemId = '"
            + imported.ImageItemId.ToString("D") + "' AND Source = 'Ocr';") >= 1);
    }

    [Fact]
    public async Task Reanalysis_AtomicallySupersedesOnlyStalePendingCandidates()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor());
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "deadline.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        using var signal = new AnalysisQueueWakeSignal();
        var store = new SqliteAnalysisJobStore(root.Paths);
        var firstWorker = new AnalysisWorker(
            "first-candidate-worker",
            store,
            storage,
            new FakeOcrProvider(),
            new ExtractiveTextComposer(),
            signal,
            entityExtractor: new FixedEntityExtractor("2026-09-11"));

        Assert.True(await firstWorker.ProcessNextAsync());

        var reanalysis = new SqliteAnalysisReanalysisService(
            root.Paths,
            new FixedProfileProvider(ModelProfileSnapshot.Default),
            signal);
        Assert.Equal(
            1,
            (await reanalysis.QueueAsync([imported.ImageItemId])).QueuedCount);
        var secondWorker = new AnalysisWorker(
            "second-candidate-worker",
            store,
            storage,
            new FakeOcrProvider(),
            new ExtractiveTextComposer(),
            signal,
            entityExtractor: new FixedEntityExtractor("2026-09-12"));

        Assert.True(await secondWorker.ProcessNextAsync());

        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE CandidateStatus = 1 AND NormalizedValue = '2026-09-12';"));
        Assert.Equal(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE CandidateStatus = 3 AND NormalizedValue = '2026-09-11';"));
        Assert.Equal(0L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM EntityCandidates WHERE CandidateStatus = 1 AND NormalizedValue = '2026-09-11';"));
    }

    [Fact]
    public async Task ExpiredLease_IsRecoveredByAnotherWorker()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "lease.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var store = new SqliteAnalysisJobStore(root.Paths);
        var now = DateTimeOffset.UtcNow;

        var first = await store.TryLeaseNextAsync(
            "worker-one",
            now,
            TimeSpan.FromSeconds(1),
            maximumAttempts: 3);
        var recovered = await store.TryLeaseNextAsync(
            "worker-two",
            now.AddSeconds(2),
            TimeSpan.FromMinutes(1),
            maximumAttempts: 3);

        Assert.NotNull(first.Lease);
        Assert.NotNull(recovered.Lease);
        Assert.Equal(first.Lease.JobId, recovered.Lease.JobId);
        Assert.Equal(2, recovered.Lease.AttemptCount);
        await store.AbandonAsync(
            "worker-two",
            recovered.Lease,
            now.AddSeconds(2));
    }

    [Fact]
    public async Task SavedOcrCheckpoint_IsResumedWithoutRunningProviderAgain()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "resume.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var store = new SqliteAnalysisJobStore(root.Paths);
        var now = DateTimeOffset.UtcNow;
        var lease = (await store.TryLeaseNextAsync(
            "checkpoint-writer",
            now,
            TimeSpan.FromMinutes(1),
            maximumAttempts: 3)).Lease!;
        var document = FakeOcrProvider.CreateDocument();
        await store.SaveCheckpointAsync(
            "checkpoint-writer",
            new AnalysisStageCheckpoint(
                Guid.NewGuid(),
                lease.JobId,
                lease.ImageItemId,
                AnalysisStage.Ocr,
                lease.InputRevision,
                document.Provenance,
                document.LanguageTags,
                JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                document.Text,
                document.Warnings,
                now),
            now.AddMinutes(1));
        await store.AbandonAsync("checkpoint-writer", lease, now);
        var provider = new ThrowingOcrProvider();
        using var signal = new AnalysisQueueWakeSignal();
        var worker = new AnalysisWorker(
            "resume-worker",
            store,
            storage,
            provider,
            new ExtractiveTextComposer(),
            signal);

        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(0, provider.CallCount);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(4L, await ScalarAsync(connection, "SELECT COUNT(*) FROM AnalysisStageResults;"));
    }

    [Fact]
    public async Task LateAnalysisCompletion_DoesNotOverwriteUserEditedFields()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "revision.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var store = new SqliteAnalysisJobStore(root.Paths);
        var now = DateTimeOffset.UtcNow;
        var lease = (await store.TryLeaseNextAsync(
            "late-worker",
            now,
            TimeSpan.FromMinutes(1),
            maximumAttempts: 3)).Lease!;
        var library = new LibraryService(root.Paths, storage);
        await library.UpdateUserFieldsAsync(imported.ImageItemId, "人工标题", "人工简介");
        var provenance = new AnalysisProvenance(
            "local.extractive-text",
            null,
            null,
            new Dictionary<string, string>(),
            "extractive-text.v1");
        var draft = new ExtractiveContentDraft("模型标题", "模型简介", ["zh-Hans"], [], provenance);
        var checkpoint = new AnalysisStageCheckpoint(
            Guid.NewGuid(),
            lease.JobId,
            lease.ImageItemId,
            AnalysisStage.TextComposition,
            lease.InputRevision,
            provenance,
            ["zh-Hans"],
            JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            "模型标题\n模型简介",
            [],
            now);

        await store.CompleteAsync("late-worker", lease, checkpoint, draft, now);

        var entry = await library.GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal("人工标题", entry.Item.Title);
        Assert.Equal("人工简介", entry.Item.Summary);
        Assert.Equal(ContentFieldSource.User, entry.Item.TitleSource);
        Assert.Equal(ContentFieldSource.User, entry.Item.SummarySource);
        Assert.Equal(AnalysisState.Completed, entry.Item.AnalysisState);
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

    private static ModelProfileSnapshot CreateRemoteVisionProfile(long revision = 3) =>
        ModelProfileSnapshot.Default with
        {
            Revision = revision,
            ExecutionBackend = AnalysisExecutionBackend.RemoteApi,
            RemoteInputMode = RemoteInputMode.DirectImage,
            RemoteApiProfile = new RemoteApiProfileSnapshot
            {
                ProfileId = "test-remote-vision",
                ProviderId = "opaque.remote-provider",
                EndpointId = "fixed.test",
                BaseUri = new Uri("https://api.example.test/v1/"),
                ModelId = "remote-model",
                PromptVersion = "prompt.v1",
                OutputSchemaVersion = QwenStructuredOutputParser.SchemaVersion,
                MaxTextChars = 10_000,
                MaxImageBytes = 1_000_000,
                MaxOutputTokens = 1_000,
                TimeoutSeconds = 30,
                CredentialReference = "credential-ref",
                ConsentVersion = "consent.v1",
            },
        };

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
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

    private sealed class FakeOcrProvider : IOcrProvider
    {
        public int CallCount { get; private set; }

        public OcrProviderDescriptor Descriptor { get; } = new(
            "fake.local-ocr",
            "Fake local OCR",
            ["zh-Hans"],
            ["Hans"],
            true);

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public Task<OcrDocument> RecognizeAsync(
            OcrRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(CreateDocument());
        }

        public static OcrDocument CreateDocument()
        {
            var titleBox = new OcrBoundingBox(0, 0, 100, 20);
            var summaryBox = new OcrBoundingBox(0, 30, 180, 20);
            return new OcrDocument(
                "项目评审会议\n7月20日 14:30 会议室A",
                [
                    new OcrLine("项目评审会议", titleBox, [new OcrWord("项目评审会议", titleBox, 1)], 1),
                    new OcrLine(
                        "7月20日 14:30 会议室A",
                        summaryBox,
                        [new OcrWord("7月20日 14:30 会议室A", summaryBox, 1)],
                        1),
                ],
                ["zh-Hans"],
                [],
                new AnalysisProvenance(
                    "fake.local-ocr",
                    "fake-model",
                    "1",
                    new Dictionary<string, string> { ["fake"] = new string('a', 64) },
                    "test.v1",
                    AnalysisExecutionLocation.Local,
                    AnalysisOutputKind.OcrFacts),
                320,
                200);
        }
    }

    private sealed class ThrowingOcrProvider : IOcrProvider
    {
        public int CallCount { get; private set; }

        public OcrProviderDescriptor Descriptor { get; } = new(
            "throwing",
            "Throwing",
            ["zh-Hans"],
            ["Hans"],
            true);

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public Task<OcrDocument> RecognizeAsync(
            OcrRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("The saved OCR checkpoint should have been reused.");
        }
    }

    private sealed class FixedEntityExtractor(string normalizedDate) : IEntityExtractor
    {
        public EntityExtractionResult Extract(
            OcrDocument ocrDocument,
            DateTimeOffset referenceTimeUtc,
            string timeZoneId) =>
            new(
                [
                    new EntityCandidateDraft(
                        "DateTime",
                        normalizedDate,
                        normalizedDate,
                        $"截止日期 {normalizedDate}",
                        "Ocr")
                    {
                        ReferenceTimeUtc = referenceTimeUtc,
                        TimeZoneId = timeZoneId,
                    },
                ],
                ocrDocument.LanguageTags,
                [],
                new AnalysisProvenance(
                    "test.fixed-entities",
                    null,
                    null,
                    new Dictionary<string, string>(),
                    "test.fixed-entities.v1",
                    AnalysisExecutionLocation.Local,
                    AnalysisOutputKind.DeterministicEntityCandidates));
    }

    private sealed class NeverCalledEntityExtractor : IEntityExtractor
    {
        public int CallCount { get; private set; }

        public EntityExtractionResult Extract(
            OcrDocument ocrDocument,
            DateTimeOffset referenceTimeUtc,
            string timeZoneId)
        {
            CallCount++;
            throw new InvalidOperationException(
                "Deterministic entity extraction must not run in RemoteVision mode.");
        }
    }

    private sealed class FixedProfileProvider(ModelProfileSnapshot profile) : IAnalysisProfileSnapshotProvider
    {
        public Task<ModelProfileSnapshot> GetCurrentSnapshotAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(profile);
    }

    private sealed class FailingVisionProvider : IVisionCaptionProvider
    {
        public Task<bool> IsAvailableAsync(
            ModelProfileSnapshot profileSnapshot,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<VisionStructuredResult> AnalyzeAsync(
            VisionAnalysisRequest request,
            CancellationToken cancellationToken = default) =>
            throw new QwenStructuredOutputException("qwen.output-token-limit-exceeded");
    }

    private sealed class ModelEntityVisionProvider : IVisionCaptionProvider
    {
        private static readonly AnalysisProvenance Provenance = new(
            "local.qwen3-vl",
            "qwen-test",
            "1",
            new Dictionary<string, string>(StringComparer.Ordinal),
            "picforlater.analysis.v1",
            AnalysisExecutionLocation.Local,
            AnalysisOutputKind.ModelGeneratedDraft);

        public int CallCount { get; private set; }

        public Task<bool> IsAvailableAsync(
            ModelProfileSnapshot profileSnapshot,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<VisionStructuredResult> AnalyzeAsync(
            VisionAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var draft = new ExtractiveContentDraft(
                "模型标题",
                "模型简介。",
                ["zh-Hans"],
                [],
                Provenance)
            {
                EntityCandidates =
                [
                    new EntityCandidateDraft(
                        "DateTime",
                        "次日 09:00",
                        "2026-07-21T09:00:00",
                        "次日复盘 7月21日 09:00",
                        "Model")
                    {
                        ReferenceTimeUtc = request.ReferenceTimeUtc,
                        TimeZoneId = request.TimeZoneId,
                        AmbiguityReason = "ModelInterpretation",
                    },
                ],
            };
            return Task.FromResult(new VisionStructuredResult(
                ["会议通知截图"],
                draft,
                ["zh-Hans"],
                [],
                Provenance));
        }
    }

    private sealed class GeneratedDraftVisionProvider(Guid categoryId) : IVisionCaptionProvider
    {
        private static readonly AnalysisProvenance Provenance = new(
            "opaque.generator",
            "generic-model",
            "1",
            new Dictionary<string, string>(StringComparer.Ordinal),
            "generic-analysis.v1",
            AnalysisExecutionLocation.Local,
            AnalysisOutputKind.ModelGeneratedDraft);

        public Task<bool> IsAvailableAsync(
            ModelProfileSnapshot profileSnapshot,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<VisionStructuredResult> AnalyzeAsync(
            VisionAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            var draft = new ExtractiveContentDraft(
                "通用模型标题",
                "通用模型简介。",
                ["zh-Hans"],
                [],
                Provenance)
            {
                SuggestedCategoryIds = [categoryId],
            };
            return Task.FromResult(new VisionStructuredResult(
                ["通用模型视觉事实"],
                draft,
                ["zh-Hans"],
                [],
                Provenance));
        }
    }

    private sealed class RemoteOcrTextVisionProvider : IVisionCaptionProvider
    {
        private static readonly AnalysisProvenance Provenance = new(
            "opaque.remote-provider",
            "remote-model",
            ModelVersion: null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            QwenStructuredOutputParser.SchemaVersion,
            AnalysisExecutionLocation.RemoteApi,
            AnalysisOutputKind.ModelGeneratedDraft,
            RemoteInputMode.LocalOcrText);

        public int CallCount { get; private set; }

        public Task<bool> IsAvailableAsync(
            ModelProfileSnapshot profileSnapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<VisionStructuredResult> AnalyzeAsync(
            VisionAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Equal(AnalysisMode.OcrOnly, request.ProfileSnapshot.AnalysisMode);
            Assert.Equal(
                "项目评审会议\n7月20日 14:30 会议室A",
                request.OcrDocument.Text);
            var draft = new ExtractiveContentDraft(
                "远程会议草稿",
                "远程模型根据本地OCR文字生成会议简介。",
                ["zh-Hans"],
                [],
                Provenance)
            {
                EntityCandidates =
                [
                    new EntityCandidateDraft(
                        "Location",
                        "会议室A",
                        null,
                        "会议室A",
                        "Model"),
                ],
            };
            return Task.FromResult(new VisionStructuredResult(
                [],
                draft,
                ["zh-Hans"],
                [],
                Provenance));
        }
    }

    private sealed class FailingRemoteOcrTextProvider : IVisionCaptionProvider
    {
        public Task<bool> IsAvailableAsync(
            ModelProfileSnapshot profileSnapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<VisionStructuredResult> AnalyzeAsync(
            VisionAnalysisRequest request,
            CancellationToken cancellationToken = default) =>
            throw new RemoteAnalysisProviderException(
                "remote.server-failure",
                isRetryable: true);
    }

    private sealed class RemoteDirectImageVisionProvider(Func<Task>? beforeReturn = null)
        : IVisionCaptionProvider
    {
        private static readonly AnalysisProvenance Provenance = new(
            "opaque.remote-provider",
            "remote-model",
            ModelVersion: null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            QwenStructuredOutputParser.SchemaVersion,
            AnalysisExecutionLocation.RemoteApi,
            AnalysisOutputKind.ModelGeneratedDraft,
            RemoteInputMode.DirectImage);

        public int CallCount { get; private set; }

        public Task<bool> IsAvailableAsync(
            ModelProfileSnapshot profileSnapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public async Task<VisionStructuredResult> AnalyzeAsync(
            VisionAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.Equal(
                AnalysisStageOutcome.SkippedByRemoteDirectImage,
                request.OcrDocument.Provenance.StageOutcome);
            Assert.Equal(RemoteInputMode.DirectImage, request.OcrDocument.Provenance.RemoteInputMode);
            Assert.Empty(request.OcrDocument.Text);
            Assert.Empty(request.OcrDocument.Lines);
            Assert.Empty(request.CompositionContext.Categories);
            await using (var image = await request.OpenImageAsync(cancellationToken))
            {
                Assert.True(image.CanRead);
            }

            if (beforeReturn is not null)
            {
                await beforeReturn();
            }

            var candidate = new EntityCandidateDraft(
                "DateTime",
                "7月20日 14:30",
                null,
                "图片中显示7月20日 14:30",
                "Model")
            {
                BoundingBox = null,
                ReferenceTimeUtc = request.ReferenceTimeUtc,
                TimeZoneId = request.TimeZoneId,
                AmbiguityReason = "RemoteVisionNoLocalOcrEvidence",
            };
            var draft = new ExtractiveContentDraft(
                "远程图片草稿",
                "远程视觉模型生成的图片简介。",
                ["zh-Hans"],
                ["remote.direct-image-no-local-ocr-evidence"],
                Provenance)
            {
                EntityCandidates = [candidate],
                SuggestedCategoryIds = [],
            };
            return new VisionStructuredResult(
                ["图片中可见会议通知。"],
                draft,
                ["zh-Hans"],
                draft.Warnings,
                Provenance);
        }
    }

    private sealed class FailingRemoteVisionProvider : IVisionCaptionProvider
    {
        public Task<bool> IsAvailableAsync(
            ModelProfileSnapshot profileSnapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<VisionStructuredResult> AnalyzeAsync(
            VisionAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(
                AnalysisStageOutcome.SkippedByRemoteDirectImage,
                request.OcrDocument.Provenance.StageOutcome);
            throw new RemoteAnalysisProviderException(
                "remote.server-failure",
                isRetryable: true);
        }
    }

    private sealed class NeverCalledVisionProvider : IVisionCaptionProvider
    {
        public int CallCount { get; private set; }

        public Task<bool> IsAvailableAsync(
            ModelProfileSnapshot profileSnapshot,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Local vision must not be queried in remote mode.");
        }

        public Task<VisionStructuredResult> AnalyzeAsync(
            VisionAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Local vision must not run in remote mode.");
        }
    }
}
