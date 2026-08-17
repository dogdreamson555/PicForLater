using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class ConditionalAnalysisRouterTests
{
    private readonly ConditionalAnalysisRouter _router = new();

    [Fact]
    public void OcrOnly_NeverRunsEnhancedAnalysis()
    {
        var decision = _router.Decide(new AnalysisRoutingRequest(
            AnalysisMode.OcrOnly,
            EnhancedProviderAvailable: true,
            CreateDocument("short text", confidence: 0.2)));

        Assert.False(decision.RunEnhancedAnalysis);
        Assert.Equal("router.ocr-only", decision.ReasonCode);
    }

    [Fact]
    public void AlwaysEnhance_RunsWhenProviderIsAvailable()
    {
        var decision = _router.Decide(new AnalysisRoutingRequest(
            AnalysisMode.AlwaysEnhance,
            EnhancedProviderAvailable: true,
            CreateDocument(new string('x', 100), confidence: 1)));

        Assert.True(decision.RunEnhancedAnalysis);
        Assert.Equal("router.always-enhance", decision.ReasonCode);
    }

    [Fact]
    public void Balanced_DenseHighConfidenceTextStaysOcrOnly()
    {
        var decision = _router.Decide(new AnalysisRoutingRequest(
            AnalysisMode.Balanced,
            EnhancedProviderAvailable: true,
            CreateDocument(new string('x', 80), confidence: 0.95)));

        Assert.False(decision.RunEnhancedAnalysis);
        Assert.Equal("router.ocr-sufficient", decision.ReasonCode);
    }

    [Theory]
    [InlineData("photo", 0.95, "router.low-text")]
    [InlineData("This OCR text is deliberately long enough to exceed the routing threshold.", 0.2, "router.low-ocr-confidence")]
    public void Balanced_RunsForLowTextOrLowConfidence(string text, double confidence, string reason)
    {
        var decision = _router.Decide(new AnalysisRoutingRequest(
            AnalysisMode.Balanced,
            EnhancedProviderAvailable: true,
            CreateDocument(text, confidence)));

        Assert.True(decision.RunEnhancedAnalysis);
        Assert.Equal(reason, decision.ReasonCode);
    }

    [Fact]
    public void MissingProvider_IsVisibleInDecision()
    {
        var decision = _router.Decide(new AnalysisRoutingRequest(
            AnalysisMode.Balanced,
            EnhancedProviderAvailable: false,
            CreateDocument(string.Empty, confidence: null)));

        Assert.False(decision.RunEnhancedAnalysis);
        Assert.Equal("router.enhanced-provider-unavailable", decision.ReasonCode);
    }

    [Fact]
    public void Thresholds_AreConfigurableAndRemainExplainable()
    {
        var router = new ConditionalAnalysisRouter(new ConditionalAnalysisRouterOptions(
            LowTextElementThreshold: 10,
            LowConfidenceThreshold: 0.1));

        var decision = router.Decide(new AnalysisRoutingRequest(
            AnalysisMode.Balanced,
            EnhancedProviderAvailable: true,
            CreateDocument("twelve chars", confidence: 0.2)));

        Assert.False(decision.RunEnhancedAnalysis);
        Assert.Equal("router.ocr-sufficient", decision.ReasonCode);
    }

    private static OcrDocument CreateDocument(string text, double? confidence)
    {
        var box = new OcrBoundingBox(0, 0, 100, 20);
        return new OcrDocument(
            text,
            string.IsNullOrEmpty(text) ? [] : [new OcrLine(text, box, [], confidence)],
            ["en"],
            [],
            new AnalysisProvenance("test.ocr", null, null, new Dictionary<string, string>(), "test.v1"),
            100,
            100);
    }
}
