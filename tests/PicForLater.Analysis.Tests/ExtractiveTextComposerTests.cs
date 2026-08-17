using PicForLater.Analysis;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class ExtractiveTextComposerTests
{
    [Fact]
    public void Compose_UsesFirstOcrLineAndPreservesUnicodeWithoutTranslation()
    {
        var ocr = CreateDocument(
            new OcrLine("專案評審會議", new OcrBoundingBox(0, 0, 100, 20), [], 0.9),
            new OcrLine("7月20日 14:30 會議室A", new OcrBoundingBox(0, 30, 160, 20), [], 0.9));

        var draft = new ExtractiveTextComposer().Compose("fallback.png", ocr);

        Assert.Equal("專案評審會議", draft.Title);
        Assert.Equal("7月20日 14:30 會議室A", draft.Summary);
        Assert.Equal(["zh-Hant"], draft.LanguageTags);
        Assert.Equal("local.extractive-text", draft.Provenance.ProviderId);
        Assert.Equal(AnalysisExecutionLocation.Local, draft.Provenance.ExecutionLocation);
        Assert.Equal(AnalysisOutputKind.ExtractiveDraft, draft.Provenance.OutputKind);
    }

    [Fact]
    public void Compose_FallsBackToFileNameWhenOcrHasNoUsableText()
    {
        var draft = new ExtractiveTextComposer().Compose(
            "offline-note.png",
            CreateDocument());

        Assert.Equal("offline-note", draft.Title);
        Assert.Empty(draft.Summary);
        Assert.Contains("extractive-title-used-file-name", draft.Warnings);
    }

    private static OcrDocument CreateDocument(params OcrLine[] lines) => new(
        string.Join(Environment.NewLine, lines.Select(line => line.Text)),
        lines,
        ["zh-Hant"],
        [],
        new AnalysisProvenance(
            "fake.ocr",
            null,
            null,
            new Dictionary<string, string>(),
            "test.v1"),
        320,
        200);
}
