using PicForLater.Analysis.Benchmarking;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class GoldenOcrBenchmarkTests
{
    [Fact]
    public async Task GoldenSuite_CoversSupportedAndExplicitlyUnsupportedScripts()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "GoldenSamples");
        var report = await GoldenOcrBenchmark.RunAsync(new GoldenFakeProvider(), directory);

        Assert.Equal(7, report.Samples.Count);
        Assert.Equal(0, report.MeanCharacterErrorRate);
        Assert.Equal(1, report.MeanFieldRecall);
        Assert.All(report.Samples, sample => Assert.True(sample.SupportExpectationMet));
        Assert.Contains(report.Samples, sample => sample.Id == "ar-unsupported" && sample.CharacterErrorRate is null);
        Assert.Contains(report.Samples, sample => sample.Id == "th-unsupported-no-spaces" && sample.CharacterErrorRate is null);
    }

    [Theory]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "adc", 1d / 3d)]
    [InlineData("会议", "會議", 1)]
    public void CharacterErrorRate_IsUnicodeAware(string expected, string actual, double value)
    {
        Assert.Equal(value, GoldenOcrBenchmark.CalculateCharacterErrorRate(expected, actual), precision: 6);
    }

    private sealed class GoldenFakeProvider : IOcrProvider
    {
        private static readonly Dictionary<string, string> TextByFile = new(StringComparer.Ordinal)
        {
            ["zh-hans-event.png"] = "项目评审会议\n7月20日 14:30 会议室A",
            ["zh-hant-event.png"] = "專案評審會議\n7月20日 14:30 會議室A",
            ["en-event.png"] = "Project review meeting\nJuly 20 2:30 PM Room A",
            ["vi-latin-extended.png"] = "Cuộc họp đánh giá dự án\n20 tháng 7 14:30 Phòng A",
            ["ja-event.png"] = "プロジェクトレビュー会議\n7月20日 14:30 会議室A",
        };

        public OcrProviderDescriptor Descriptor { get; } = new(
            "fake.ppocr",
            "Fake PP-OCR",
            ["zh-Hans", "zh-Hant", "en", "vi", "ja"],
            ["Hans", "Hant", "Latn", "Jpan"],
            true);

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public Task<OcrDocument> RecognizeAsync(
            OcrRequest request,
            CancellationToken cancellationToken = default)
        {
            var text = TextByFile[request.OriginalFileName];
            var lines = text.Split('\n').Select((line, index) =>
                new OcrLine(
                    line,
                    new OcrBoundingBox(10, 10 + (index * 40), 300, 30),
                    [new OcrWord(line, new OcrBoundingBox(10, 10 + (index * 40), 300, 30), 1)],
                    1)).ToArray();
            return Task.FromResult(new OcrDocument(
                text,
                lines,
                request.LanguageHints,
                [],
                new AnalysisProvenance(
                    Descriptor.ProviderId,
                    "pp-ocrv6-small",
                    "fake",
                    new Dictionary<string, string>(),
                    "test.v1"),
                1200,
                360));
        }
    }
}
