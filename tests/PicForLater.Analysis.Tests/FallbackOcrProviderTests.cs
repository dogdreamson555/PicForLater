using PicForLater.Analysis;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class FallbackOcrProviderTests
{
    [Fact]
    public async Task Recognize_UsesNextLocalProviderAndRecordsFallbackWarning()
    {
        var provider = new FallbackOcrProvider(
        [
            new StubProvider(
                "enhanced",
                _ => throw new OcrProviderException("enhanced.failed", isRetryable: false)),
            new StubProvider("windows", _ => CreateDocument("local result", "windows")),
        ]);

        var result = await provider.RecognizeAsync(CreateRequest());

        Assert.Equal("local result", result.Text);
        Assert.Equal("windows", result.Provenance.ProviderId);
        Assert.Contains("ocr-provider-fallback:enhanced:enhanced.failed", result.Warnings);
    }

    private static OcrRequest CreateRequest() => new(
        _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false)),
        "sample.png",
        1,
        1,
        []);

    private static OcrDocument CreateDocument(string text, string providerId) => new(
        text,
        [],
        ["en"],
        [],
        new AnalysisProvenance(
            providerId,
            null,
            null,
            new Dictionary<string, string>(),
            "test.v1"),
        1,
        1);

    private sealed class StubProvider(
        string providerId,
        Func<OcrRequest, OcrDocument> recognize) : IOcrProvider
    {
        public OcrProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            ["en"],
            ["Latn"],
            false);

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public Task<OcrDocument> RecognizeAsync(
            OcrRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(recognize(request));
    }
}
