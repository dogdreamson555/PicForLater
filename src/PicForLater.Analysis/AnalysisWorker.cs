using System.Text.Json;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;

namespace PicForLater.Analysis;

public sealed record AnalysisWorkerOptions(
    TimeSpan LeaseDuration,
    TimeSpan OcrTimeout,
    int MaximumAttempts)
{
    public TimeSpan VisionTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public static AnalysisWorkerOptions Default { get; } = new(
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(2),
        MaximumAttempts: 3);
}

public sealed class AnalysisWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _workerId;
    private readonly IAnalysisJobStore _store;
    private readonly IManagedImageStorage _imageStorage;
    private readonly IOcrProvider _ocrProvider;
    private readonly ITextComposer _textComposer;
    private readonly IEntityExtractor _entityExtractor;
    private readonly IVisionCaptionProvider _visionProvider;
    private readonly IVisionCaptionProvider _remoteOcrTextProvider;
    private readonly IVisionCaptionProvider _remoteVisionProvider;
    private readonly IAnalysisRouter _router;
    private readonly AnalysisQueueWakeSignal _wakeSignal;
    private readonly AnalysisWorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ReminderCandidateMerger _candidateMerger = new();

    public AnalysisWorker(
        string workerId,
        IAnalysisJobStore store,
        IManagedImageStorage imageStorage,
        IOcrProvider ocrProvider,
        ITextComposer textComposer,
        AnalysisQueueWakeSignal wakeSignal,
        IVisionCaptionProvider? visionProvider = null,
        IAnalysisRouter? router = null,
        IEntityExtractor? entityExtractor = null,
        AnalysisWorkerOptions? options = null,
        TimeProvider? timeProvider = null,
        IVisionCaptionProvider? remoteOcrTextProvider = null,
        IVisionCaptionProvider? remoteVisionProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        _workerId = workerId;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _imageStorage = imageStorage ?? throw new ArgumentNullException(nameof(imageStorage));
        _ocrProvider = ocrProvider ?? throw new ArgumentNullException(nameof(ocrProvider));
        _textComposer = textComposer ?? throw new ArgumentNullException(nameof(textComposer));
        _entityExtractor = entityExtractor ?? new DeterministicEntityExtractor();
        _visionProvider = visionProvider ?? UnavailableVisionCaptionProvider.Instance;
        _remoteOcrTextProvider = remoteOcrTextProvider
            ?? UnavailableVisionCaptionProvider.Instance;
        _remoteVisionProvider = remoteVisionProvider
            ?? UnavailableVisionCaptionProvider.Instance;
        _router = router ?? new ConditionalAnalysisRouter();
        _wakeSignal = wakeSignal ?? throw new ArgumentNullException(nameof(wakeSignal));
        _options = options ?? AnalysisWorkerOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_options.LeaseDuration <= TimeSpan.Zero
            || _options.OcrTimeout <= TimeSpan.Zero
            || _options.VisionTimeout <= TimeSpan.Zero
            || _options.MaximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leaseAttempt = await _store.TryLeaseNextAsync(
                _workerId,
                _timeProvider.GetUtcNow(),
                _options.LeaseDuration,
                _options.MaximumAttempts,
                cancellationToken).ConfigureAwait(false);
            if (leaseAttempt.Lease is null)
            {
                await _wakeSignal.WaitAsync(
                    leaseAttempt.NextWakeAtUtc,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            await ProcessLeaseAsync(leaseAttempt.Lease, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var leaseAttempt = await _store.TryLeaseNextAsync(
            _workerId,
            _timeProvider.GetUtcNow(),
            _options.LeaseDuration,
            _options.MaximumAttempts,
            cancellationToken).ConfigureAwait(false);
        if (leaseAttempt.Lease is null)
        {
            return false;
        }

        await ProcessLeaseAsync(leaseAttempt.Lease, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ProcessLeaseAsync(
        AnalysisJobLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            var isRemoteOcrText =
                lease.ProfileSnapshot.ExecutionBackend == AnalysisExecutionBackend.RemoteApi
                && lease.ProfileSnapshot.RemoteInputMode == RemoteInputMode.LocalOcrText
                && lease.ProfileSnapshot.RemoteApiProfile is not null;
            var isRemoteVision =
                lease.ProfileSnapshot.ExecutionBackend == AnalysisExecutionBackend.RemoteApi
                && lease.ProfileSnapshot.RemoteInputMode == RemoteInputMode.DirectImage
                && lease.ProfileSnapshot.RemoteApiProfile is not null;
            if (lease.ProfileSnapshot.ExecutionBackend != AnalysisExecutionBackend.Local
                && !isRemoteOcrText
                && !isRemoteVision)
            {
                throw new AnalysisExecutionUnavailableException(
                    "remote.profile-snapshot-invalid");
            }

            var isUntampered = await _imageStorage.VerifyAsync(
                lease.OriginalRelativePath,
                lease.ContentHash,
                cancellationToken).ConfigureAwait(false);
            if (!isUntampered)
            {
                throw new InvalidDataException("The managed original did not pass integrity verification.");
            }

            var ocrCheckpoint = await _store.GetCheckpointAsync(
                lease.JobId,
                AnalysisStage.Ocr,
                cancellationToken).ConfigureAwait(false);
            OcrDocument ocrDocument;
            if (isRemoteVision
                && ocrCheckpoint?.Provenance.StageOutcome
                    != AnalysisStageOutcome.SkippedByRemoteDirectImage)
            {
                var generatedAtUtc = _timeProvider.GetUtcNow();
                ocrDocument = CreateRemoteVisionSkippedOcrDocument(lease);
                ocrCheckpoint = CreateCheckpoint(
                    lease,
                    AnalysisStage.Ocr,
                    ocrDocument.Provenance,
                    ocrDocument.LanguageTags,
                    JsonSerializer.Serialize(ocrDocument, JsonOptions),
                    string.Empty,
                    ocrDocument.Warnings,
                    generatedAtUtc);
                await _store.SaveCheckpointAsync(
                    _workerId,
                    ocrCheckpoint,
                    generatedAtUtc.Add(_options.LeaseDuration),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (ocrCheckpoint is null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.OcrTimeout);
                var request = new OcrRequest(
                    openCancellationToken => new ValueTask<Stream>(
                        _imageStorage.OpenReadAsync(lease.OriginalRelativePath, openCancellationToken)),
                    lease.OriginalFileName,
                    lease.PixelWidth,
                    lease.PixelHeight,
                    LanguageHints: [])
                {
                    ManagedImage = new ManagedAnalysisImage(
                        lease.OriginalRelativePath,
                        lease.ContentHash),
                };
                ocrDocument = await _ocrProvider.RecognizeAsync(request, timeout.Token).ConfigureAwait(false);
                var generatedAtUtc = _timeProvider.GetUtcNow();
                ocrCheckpoint = CreateCheckpoint(
                    lease,
                    AnalysisStage.Ocr,
                    ocrDocument.Provenance,
                    ocrDocument.LanguageTags,
                    JsonSerializer.Serialize(ocrDocument, JsonOptions),
                    ocrDocument.Text,
                    ocrDocument.Warnings,
                    generatedAtUtc);
                await _store.SaveCheckpointAsync(
                    _workerId,
                    ocrCheckpoint,
                    generatedAtUtc.Add(_options.LeaseDuration),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var savedOcrDocument = JsonSerializer.Deserialize<OcrDocument>(
                        ocrCheckpoint.PayloadJson,
                        JsonOptions)
                    ?? throw new JsonException("The OCR checkpoint payload is empty.");
                ocrDocument = savedOcrDocument with
                {
                    Provenance = ocrCheckpoint.Provenance,
                };
            }

            var entityCheckpoint = await _store.GetCheckpointAsync(
                lease.JobId,
                AnalysisStage.DeterministicEntities,
                cancellationToken).ConfigureAwait(false);
            EntityExtractionResult entityResult;
            if (isRemoteVision
                && entityCheckpoint?.Provenance.StageOutcome
                    != AnalysisStageOutcome.SkippedByRemoteDirectImage)
            {
                var generatedAtUtc = _timeProvider.GetUtcNow();
                entityResult = CreateRemoteVisionSkippedEntityResult();
                entityCheckpoint = CreateCheckpoint(
                    lease,
                    AnalysisStage.DeterministicEntities,
                    entityResult.Provenance,
                    entityResult.LanguageTags,
                    JsonSerializer.Serialize(entityResult, JsonOptions),
                    string.Empty,
                    entityResult.Warnings,
                    generatedAtUtc);
                await _store.SaveCheckpointAsync(
                    _workerId,
                    entityCheckpoint,
                    generatedAtUtc.Add(_options.LeaseDuration),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (entityCheckpoint is null)
            {
                var generatedAtUtc = _timeProvider.GetUtcNow();
                entityResult = _entityExtractor.Extract(
                    ocrDocument,
                    generatedAtUtc,
                    TimeZoneInfo.Local.Id);
                entityCheckpoint = CreateCheckpoint(
                    lease,
                    AnalysisStage.DeterministicEntities,
                    entityResult.Provenance,
                    entityResult.LanguageTags,
                    JsonSerializer.Serialize(entityResult, JsonOptions),
                    string.Join(
                        Environment.NewLine,
                        entityResult.Candidates.Select(candidate => candidate.RawText)),
                    entityResult.Warnings,
                    generatedAtUtc);
                await _store.SaveCheckpointAsync(
                    _workerId,
                    entityCheckpoint,
                    generatedAtUtc.Add(_options.LeaseDuration),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var savedEntityResult = JsonSerializer.Deserialize<EntityExtractionResult>(
                        entityCheckpoint.PayloadJson,
                        JsonOptions)
                    ?? throw new JsonException("The entity checkpoint payload is empty.");
                entityResult = savedEntityResult with
                {
                    Provenance = entityCheckpoint.Provenance,
                };
            }

            var visionCheckpoint = await _store.GetCheckpointAsync(
                lease.JobId,
                AnalysisStage.Vision,
                cancellationToken).ConfigureAwait(false);
            VisionStagePayload visionPayload;
            if (visionCheckpoint is null)
            {
                var selectedProvider = isRemoteVision
                    ? _remoteVisionProvider
                    : isRemoteOcrText
                        ? _remoteOcrTextProvider
                        : _visionProvider;
                var enhancedAvailable = await selectedProvider.IsAvailableAsync(
                    lease.ProfileSnapshot,
                    cancellationToken).ConfigureAwait(false);
                var routing = isRemoteVision
                    ? CreateRemoteVisionRoutingDecision()
                    : isRemoteOcrText
                        ? CreateRemoteOcrTextRoutingDecision(ocrDocument)
                        : _router.Decide(new AnalysisRoutingRequest(
                            lease.ProfileSnapshot.AnalysisMode,
                            enhancedAvailable,
                            ocrDocument));
                VisionStructuredResult? visionResult = null;
                string? enhancementFailureCode = (isRemoteOcrText || isRemoteVision)
                    && !enhancedAvailable
                    ? "remote.provider-unavailable"
                    : null;
                if (routing.RunEnhancedAnalysis && enhancedAvailable)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(_options.VisionTimeout);
                        var compositionContext = isRemoteVision
                            ? new AnalysisCompositionContext([])
                            : await _store.GetCompositionContextAsync(
                                lease.ImageItemId,
                                timeout.Token).ConfigureAwait(false);
                        visionResult = await selectedProvider.AnalyzeAsync(
                            new VisionAnalysisRequest(
                                openCancellationToken => new ValueTask<Stream>(
                                    _imageStorage.OpenReadAsync(lease.OriginalRelativePath, openCancellationToken)),
                                lease.OriginalFileName,
                                ocrDocument,
                                compositionContext,
                                lease.ProfileSnapshot)
                            {
                                ReferenceTimeUtc = entityCheckpoint.GeneratedAtUtc,
                                TimeZoneId = TimeZoneInfo.Local.Id,
                                ManagedImage = new ManagedAnalysisImage(
                                    lease.OriginalRelativePath,
                                    lease.ContentHash),
                            },
                            timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        enhancementFailureCode = isRemoteOcrText || isRemoteVision
                            ? "remote.timeout"
                            : "qwen.timeout";
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        enhancementFailureCode = Classify(exception).ErrorCode;
                    }
                }

                visionPayload = new VisionStagePayload(routing, visionResult)
                {
                    FailureCode = enhancementFailureCode,
                };
                var generatedAtUtc = _timeProvider.GetUtcNow();
                var provenance = visionResult?.Provenance
                    ?? (isRemoteOcrText || isRemoteVision
                        ? CreateRemoteRoutingProvenance(
                            lease.ProfileSnapshot,
                            isRemoteVision
                                ? RemoteInputMode.DirectImage
                                : RemoteInputMode.LocalOcrText)
                        : new AnalysisProvenance(
                            "local.conditional-router",
                            ModelId: null,
                            ModelVersion: null,
                            new Dictionary<string, string>(StringComparer.Ordinal),
                            "conditional-router.v1",
                            AnalysisExecutionLocation.Local,
                            AnalysisOutputKind.RoutingDecision));
                visionCheckpoint = CreateCheckpoint(
                    lease,
                    AnalysisStage.Vision,
                    provenance,
                    visionResult?.LanguageTags ?? ocrDocument.LanguageTags,
                    JsonSerializer.Serialize(visionPayload, JsonOptions),
                    visionResult is null ? string.Empty : string.Join(Environment.NewLine, visionResult.VisualFacts),
                    visionResult?.Warnings ?? CreateVisionFallbackWarnings(routing, enhancementFailureCode),
                    generatedAtUtc);
                await _store.SaveCheckpointAsync(
                    _workerId,
                    visionCheckpoint,
                    generatedAtUtc.Add(_options.LeaseDuration),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var savedVisionPayload = JsonSerializer.Deserialize<VisionStagePayload>(
                        visionCheckpoint.PayloadJson,
                        JsonOptions)
                    ?? throw new JsonException("The vision checkpoint payload is empty.");
                visionPayload = savedVisionPayload.Result is null
                    ? savedVisionPayload
                    : savedVisionPayload with
                    {
                        Result = savedVisionPayload.Result with
                        {
                            Provenance = visionCheckpoint.Provenance,
                            Draft = savedVisionPayload.Result.Draft with
                            {
                                Provenance = visionCheckpoint.Provenance,
                            },
                        },
                    };
            }

            if (isRemoteVision && visionPayload.FailureCode is not null)
            {
                throw new AnalysisExecutionUnavailableException(visionPayload.FailureCode);
            }

            var modelEntityCandidates = visionPayload.Result?.Draft.EntityCandidates
                ?? [];
            var draft = CanUseStructuredDraft(lease.ProfileSnapshot, visionPayload.Result)
                ? visionPayload.Result!.Draft
                : _textComposer.Compose(lease.OriginalFileName, ocrDocument);
            draft = draft with
            {
                EntityCandidates = _candidateMerger.Merge(
                    entityResult.Candidates,
                    modelEntityCandidates,
                    entityCheckpoint.GeneratedAtUtc,
                    TimeZoneInfo.Local.Id,
                    entityResult.LanguageTags
                        .Concat(visionPayload.Result?.LanguageTags ?? [])
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()),
            };
            if (visionPayload.FailureCode is not null
                && draft.Provenance.OutputKind != AnalysisOutputKind.ModelGeneratedDraft)
            {
                draft = draft with
                {
                    Warnings = draft.Warnings
                        .Append($"enhancement-fallback:{visionPayload.FailureCode}")
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                };
            }
            var completedAtUtc = _timeProvider.GetUtcNow();
            var compositionCheckpoint = CreateCheckpoint(
                lease,
                AnalysisStage.TextComposition,
                draft.Provenance,
                draft.LanguageTags,
                JsonSerializer.Serialize(draft, JsonOptions),
                string.Join(Environment.NewLine, new[] { draft.Title, draft.Summary }
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
                draft.Warnings,
                completedAtUtc);
            await _store.CompleteAsync(
                _workerId,
                lease,
                compositionCheckpoint,
                draft,
                completedAtUtc,
                isRemoteOcrText && visionPayload.FailureCode is not null
                    ? new AnalysisCompletionFailure(visionPayload.FailureCode)
                    : null,
                cancellationToken).ConfigureAwait(false);
            _wakeSignal.NotifyItemChanged(lease.ImageItemId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _store.AbandonAsync(
                    _workerId,
                    lease,
                    _timeProvider.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The expiring lease remains sufficient for recovery if shutdown
                // prevents this best-effort release from reaching SQLite.
            }

            throw;
        }
        catch (AnalysisLeaseLostException)
        {
            // Another worker recovered an expired lease. Its writes are now
            // authoritative, so this stale worker must not change job state.
        }
        catch (Exception exception)
        {
            var (errorCode, retryable) = Classify(exception);
            var failedAtUtc = _timeProvider.GetUtcNow();
            await _store.FailAsync(
                _workerId,
                lease,
                errorCode,
                retryable,
                failedAtUtc.Add(GetRetryDelay(lease.AttemptCount)),
                _options.MaximumAttempts,
                failedAtUtc,
                CancellationToken.None).ConfigureAwait(false);
            _wakeSignal.NotifyItemChanged(lease.ImageItemId);
        }
    }

    private static IReadOnlyList<string> CreateVisionFallbackWarnings(
        AnalysisRoutingDecision routing,
        string? failureCode) => failureCode is null
        ? [routing.ReasonCode]
        : [routing.ReasonCode, $"enhancement-fallback:{failureCode}"];

    private static AnalysisStageCheckpoint CreateCheckpoint(
        AnalysisJobLease lease,
        AnalysisStage stage,
        AnalysisProvenance provenance,
        IReadOnlyList<string> languageTags,
        string payloadJson,
        string factText,
        IReadOnlyList<string> warnings,
        DateTimeOffset generatedAtUtc) =>
        new(
            Guid.NewGuid(),
            lease.JobId,
            lease.ImageItemId,
            stage,
            lease.InputRevision,
            provenance,
            languageTags,
            payloadJson,
            factText,
            warnings,
            generatedAtUtc);

    private static (string ErrorCode, bool Retryable) Classify(Exception exception) => exception switch
    {
        OcrProviderUnavailableException unavailable => (unavailable.ErrorCode, false),
        OcrProviderException provider => (provider.ErrorCode, provider.IsRetryable),
        AnalysisExecutionUnavailableException unavailable => (unavailable.ErrorCode, false),
        RemoteAnalysisProviderException remote => (remote.ErrorCode, remote.IsRetryable),
        QwenStructuredOutputException output => (output.ErrorCode, false),
        ModelPackageValidationException package => (package.ErrorCode, false),
        FileNotFoundException => ("analysis.original-missing", false),
        InvalidDataException => ("analysis.invalid-input", false),
        JsonException => ("analysis.invalid-checkpoint", false),
        OperationCanceledException => ("analysis.timeout", true),
        IOException => ("analysis.io-failed", true),
        _ => ("analysis.unexpected-failure", false),
    };

    private static bool CanUseStructuredDraft(
        ModelProfileSnapshot snapshot,
        VisionStructuredResult? result)
    {
        if (result?.Provenance.OutputKind != AnalysisOutputKind.ModelGeneratedDraft
            || result.Provenance.ModelId is null)
        {
            return false;
        }

        if (snapshot.ExecutionBackend == AnalysisExecutionBackend.RemoteApi)
        {
            var remote = snapshot.RemoteApiProfile;
            return (snapshot.RemoteInputMode is RemoteInputMode.LocalOcrText
                    or RemoteInputMode.DirectImage)
                && result.Provenance.ExecutionLocation == AnalysisExecutionLocation.RemoteApi
                && result.Provenance.RemoteInputMode == snapshot.RemoteInputMode
                && remote is not null
                && string.Equals(
                    result.Provenance.ProviderId,
                    remote.ProviderId,
                    StringComparison.Ordinal)
                && string.Equals(
                    result.Provenance.ModelId,
                    remote.ModelId,
                    StringComparison.Ordinal)
                && string.Equals(
                    result.Provenance.SchemaVersion,
                    remote.OutputSchemaVersion,
                    StringComparison.Ordinal);
        }

        if (result.Provenance.ModelVersion is null)
        {
            return false;
        }

        var textSlot = snapshot.GetSlot(ModelCapability.TextComposition);
        return textSlot.PackageKey == $"{result.Provenance.ModelId}@{result.Provenance.ModelVersion}";
    }

    private static AnalysisRoutingDecision CreateRemoteOcrTextRoutingDecision(
        OcrDocument ocrDocument) =>
        new(
            RunEnhancedAnalysis: true,
            ReasonCode: "remote.local-ocr-text-selected",
            OcrTextElementCount: ocrDocument.Text.Length,
            MeanOcrConfidence: ocrDocument.Lines
                .Where(line => line.Confidence.HasValue)
                .Select(line => (double?)line.Confidence!.Value)
                .Average());

    private static AnalysisRoutingDecision CreateRemoteVisionRoutingDecision() =>
        new(
            RunEnhancedAnalysis: true,
            ReasonCode: "remote.direct-image-selected",
            OcrTextElementCount: 0,
            MeanOcrConfidence: null);

    private static AnalysisProvenance CreateRemoteRoutingProvenance(
        ModelProfileSnapshot snapshot,
        RemoteInputMode inputMode)
    {
        var remote = snapshot.RemoteApiProfile
            ?? throw new InvalidDataException("The remote profile snapshot is missing.");
        return new AnalysisProvenance(
            remote.ProviderId,
            remote.ModelId,
            ModelVersion: null,
            new Dictionary<string, string>(StringComparer.Ordinal),
            remote.OutputSchemaVersion,
            AnalysisExecutionLocation.RemoteApi,
            AnalysisOutputKind.RoutingDecision,
            inputMode);
    }

    private static OcrDocument CreateRemoteVisionSkippedOcrDocument(
        AnalysisJobLease lease) =>
        new(
            Text: string.Empty,
            Lines: [],
            LanguageTags: [],
            Warnings: ["analysis.skipped-by-remote-direct-image"],
            Provenance: CreateRemoteVisionSkippedProvenance(),
            ImageWidth: lease.PixelWidth,
            ImageHeight: lease.PixelHeight);

    private static EntityExtractionResult CreateRemoteVisionSkippedEntityResult() =>
        new(
            Candidates: [],
            LanguageTags: [],
            Warnings: ["analysis.skipped-by-remote-direct-image"],
            Provenance: CreateRemoteVisionSkippedProvenance());

    private static AnalysisProvenance CreateRemoteVisionSkippedProvenance() =>
        new(
            ProviderId: "analysis.execution-router",
            ModelId: null,
            ModelVersion: null,
            ModelFileHashes: new Dictionary<string, string>(StringComparer.Ordinal),
            SchemaVersion: "remote-direct-image-skip.v1",
            ExecutionLocation: AnalysisExecutionLocation.RemoteApi,
            OutputKind: AnalysisOutputKind.Unspecified,
            RemoteInputMode: RemoteInputMode.DirectImage,
            StageOutcome: AnalysisStageOutcome.SkippedByRemoteDirectImage);

    private sealed class UnavailableVisionCaptionProvider : IVisionCaptionProvider
    {
        public static UnavailableVisionCaptionProvider Instance { get; } = new();

        public Task<bool> IsAvailableAsync(
            ModelProfileSnapshot profileSnapshot,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<VisionStructuredResult> AnalyzeAsync(
            VisionAnalysisRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The enhanced vision provider is unavailable.");
    }

    private static TimeSpan GetRetryDelay(int attemptCount) => attemptCount switch
    {
        <= 1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(2),
    };
}
