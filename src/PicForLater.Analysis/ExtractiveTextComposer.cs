using System.Globalization;
using System.Text;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

public sealed class ExtractiveTextComposer : ITextComposer
{
    private const int MaximumTitleTextElements = 80;
    private const int MaximumSummaryTextElements = 320;
    private const int MaximumSummaryLines = 4;

    public ExtractiveContentDraft Compose(string originalFileName, OcrDocument ocrDocument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentNullException.ThrowIfNull(ocrDocument);

        var lines = ocrDocument.Lines
            .Select(line => NormalizeWhitespace(line.Text))
            .Where(line => !string.IsNullOrWhiteSpace(line) && ContainsLetterOrDigit(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var fallbackTitle = NormalizeWhitespace(Path.GetFileNameWithoutExtension(originalFileName));
        if (string.IsNullOrWhiteSpace(fallbackTitle))
        {
            fallbackTitle = originalFileName;
        }

        var title = TruncateTextElements(
            lines.FirstOrDefault() ?? fallbackTitle,
            MaximumTitleTextElements);
        var summaryLines = lines.Skip(1).Take(MaximumSummaryLines).ToArray();
        var summary = TruncateTextElements(
            string.Join(Environment.NewLine, summaryLines),
            MaximumSummaryTextElements);
        var warnings = new List<string>();
        if (lines.Length == 0)
        {
            warnings.Add("extractive-title-used-file-name");
        }

        if (summary.Length == 0)
        {
            warnings.Add("extractive-summary-empty");
        }

        return new ExtractiveContentDraft(
            title,
            summary,
            ocrDocument.LanguageTags,
            warnings,
            new AnalysisProvenance(
                "local.extractive-text",
                ModelId: null,
                ModelVersion: null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "extractive-text.v1",
                AnalysisExecutionLocation.Local,
                AnalysisOutputKind.ExtractiveDraft));
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString().Trim();
    }

    private static bool ContainsLetterOrDigit(string value) =>
        value.EnumerateRunes().Any(rune =>
            Rune.IsLetterOrDigit(rune)
            || Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherLetter);

    private static string TruncateTextElements(string value, int maximumTextElements)
    {
        var textElements = StringInfo.GetTextElementEnumerator(value);
        var builder = new StringBuilder(Math.Min(value.Length, maximumTextElements));
        var count = 0;
        while (count < maximumTextElements && textElements.MoveNext())
        {
            builder.Append(textElements.GetTextElement());
            count++;
        }

        return builder.ToString().Trim();
    }
}
