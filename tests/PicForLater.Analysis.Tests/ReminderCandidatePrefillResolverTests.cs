using PicForLater.Core.Reminders;

namespace PicForLater.Analysis.Tests;

public sealed class ReminderCandidatePrefillResolverTests
{
    [Theory]
    [InlineData(
        "发售日期：2026年11月19日",
        "2026-11-19",
        null)]
    [InlineData(
        "年度总决赛&颁奖日期（预计）：2027年5月",
        "2027-05",
        "MissingDay")]
    [InlineData(
        "预计年份：2027年",
        "2027",
        "MissingMonthAndDay")]
    [InlineData(
        "申报截止时间：2026.9.11(周五)16:30",
        "2026-09-11T16:30:00",
        null)]
    public void Resolve_UsesAuditableEvidenceForLegacyModelCandidate(
        string evidence,
        string expectedNormalized,
        string? expectedAmbiguity)
    {
        var candidate = CreateCandidate(evidence);

        var result = new ReminderCandidatePrefillResolver().Resolve(candidate);

        Assert.NotNull(result);
        Assert.Equal(expectedNormalized, result.NormalizedValue);
        Assert.Equal(expectedAmbiguity, result.AmbiguityReason);
    }

    [Fact]
    public void Resolve_DoesNotOverrideAnExistingNormalizedValue()
    {
        var candidate = CreateCandidate("发售日期：2026年11月19日") with
        {
            NormalizedValue = "2026-11-19",
        };

        Assert.Null(new ReminderCandidatePrefillResolver().Resolve(candidate));
    }

    [Fact]
    public void Resolve_RepairsInvalidModelNormalizationFromCombinedEvidence()
    {
        var candidate = CreateCandidate("申报截止时间：2026.9.11(周五)16:30") with
        {
            NormalizedValue = "2026.9.11 16:30",
        };

        var result = new ReminderCandidatePrefillResolver().Resolve(candidate);

        Assert.NotNull(result);
        Assert.Equal("2026-09-11T16:30:00", result.NormalizedValue);
    }

    private static ReminderCandidate CreateCandidate(string evidence) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "图片标题",
        EntityCandidateKind.DateTime,
        evidence,
        NormalizedValue: null,
        evidence,
        EntityCandidateSource.Model,
        BoundingBoxJson: null,
        new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero),
        "China Standard Time",
        "ModelInterpretation",
        new DateTimeOffset(2026, 7, 30, 0, 1, 0, TimeSpan.Zero));
}
