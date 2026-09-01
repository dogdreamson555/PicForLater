using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class AnalysisOutputLanguageInstructionTests
{
    [Theory]
    [InlineData(
        AnalysisOutputLanguage.ModelDefault,
        "No target output language is imposed. Choose the language you consider most appropriate for the generated fields.")]
    [InlineData(
        AnalysisOutputLanguage.SimplifiedChinese,
        "Write the generated title, summary, and visualFacts in Simplified Chinese (zh-Hans).")]
    [InlineData(
        AnalysisOutputLanguage.TraditionalChineseTaiwan,
        "Write the generated title, summary, and visualFacts in Traditional Chinese as used in Taiwan (zh-Hant-TW).")]
    [InlineData(
        AnalysisOutputLanguage.English,
        "Write the generated title, summary, and visualFacts in English (en).")]
    public void Create_ReturnsStableSourceAwarePolicy(
        AnalysisOutputLanguage outputLanguage,
        string expectedTargetInstruction)
    {
        var instruction = AnalysisOutputLanguageInstruction.Create(outputLanguage);

        Assert.Equal(
            instruction,
            AnalysisOutputLanguageInstruction.Create(outputLanguage));
        Assert.Contains(expectedTargetInstruction, instruction, StringComparison.Ordinal);
        Assert.Contains("title, summary, and", instruction, StringComparison.Ordinal);
        Assert.Contains("visualFacts", instruction, StringComparison.Ordinal);
        Assert.Contains("detectedLanguages", instruction, StringComparison.Ordinal);
        Assert.Contains("entities[].rawText", instruction, StringComparison.Ordinal);
        Assert.Contains("entities[].evidence", instruction, StringComparison.Ordinal);
        Assert.Contains("verbatim from the source content", instruction, StringComparison.Ordinal);
        Assert.Contains("identifiers", instruction, StringComparison.Ordinal);
        Assert.Contains("URLs", instruction, StringComparison.Ordinal);
        Assert.Contains("times", instruction, StringComparison.Ordinal);
        Assert.Contains("model", instruction, StringComparison.Ordinal);
        Assert.Contains("numbers", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("same as content", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Preserve the content language", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ReturnsUniquePoliciesForAllContractValues()
    {
        var instructions = Enum.GetValues<AnalysisOutputLanguage>()
            .Select(AnalysisOutputLanguageInstruction.Create)
            .ToArray();

        Assert.Equal(4, instructions.Length);
        Assert.Equal(4, instructions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Create_RejectsInvalidValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AnalysisOutputLanguageInstruction.Create((AnalysisOutputLanguage)4));
    }
}
