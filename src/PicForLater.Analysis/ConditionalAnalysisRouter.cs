using System.Globalization;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

public sealed record ConditionalAnalysisRouterOptions(
    int LowTextElementThreshold = 48,
    double LowConfidenceThreshold = 0.55);

public sealed class ConditionalAnalysisRouter : IAnalysisRouter
{
    private readonly ConditionalAnalysisRouterOptions _options;

    public ConditionalAnalysisRouter(ConditionalAnalysisRouterOptions? options = null)
    {
        _options = options ?? new ConditionalAnalysisRouterOptions();
        if (_options.LowTextElementThreshold <= 0
            || _options.LowConfidenceThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public AnalysisRoutingDecision Decide(AnalysisRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OcrDocument);
        if (!Enum.IsDefined(request.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var textElementCount = new StringInfo(request.OcrDocument.Text ?? string.Empty).LengthInTextElements;
        var confidences = request.OcrDocument.Lines
            .Select(line => line.Confidence)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        double? meanConfidence = confidences.Length == 0 ? null : confidences.Average();

        if (request.Mode == AnalysisMode.OcrOnly)
        {
            return new AnalysisRoutingDecision(false, "router.ocr-only", textElementCount, meanConfidence);
        }

        if (!request.EnhancedProviderAvailable)
        {
            return new AnalysisRoutingDecision(false, "router.enhanced-provider-unavailable", textElementCount, meanConfidence);
        }

        if (request.Mode == AnalysisMode.AlwaysEnhance)
        {
            return new AnalysisRoutingDecision(true, "router.always-enhance", textElementCount, meanConfidence);
        }

        if (textElementCount < _options.LowTextElementThreshold)
        {
            return new AnalysisRoutingDecision(true, "router.low-text", textElementCount, meanConfidence);
        }

        if (meanConfidence is not null && meanConfidence < _options.LowConfidenceThreshold)
        {
            return new AnalysisRoutingDecision(true, "router.low-ocr-confidence", textElementCount, meanConfidence);
        }

        return new AnalysisRoutingDecision(false, "router.ocr-sufficient", textElementCount, meanConfidence);
    }
}
