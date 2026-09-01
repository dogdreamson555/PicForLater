using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

internal static class AnalysisOutputLanguageInstruction
{
    public static string Create(AnalysisOutputLanguage outputLanguage)
    {
        var targetInstruction = outputLanguage switch
        {
            AnalysisOutputLanguage.ModelDefault =>
                "No target output language is imposed. Choose the language you consider most appropriate for the generated fields.",
            AnalysisOutputLanguage.SimplifiedChinese =>
                "Write the generated title, summary, and visualFacts in Simplified Chinese (zh-Hans).",
            AnalysisOutputLanguage.TraditionalChineseTaiwan =>
                "Write the generated title, summary, and visualFacts in Traditional Chinese as used in Taiwan (zh-Hant-TW).",
            AnalysisOutputLanguage.English =>
                "Write the generated title, summary, and visualFacts in English (en).",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outputLanguage),
                outputLanguage,
                "The analysis output language is invalid."),
        };

        return $"""
        Output language policy:
        {targetInstruction}
        The language choice applies only to generated title, summary, and
        visualFacts. detectedLanguages must describe the languages of the source
        content, not the chosen output language. Keep every entities[].rawText and
        entities[].evidence value verbatim from the source content. Preserve names,
        brands, identifiers, URLs, addresses, dates, times, amounts, and model
        numbers in their source form whenever translating them would change the
        evidence.
        """;
    }
}
