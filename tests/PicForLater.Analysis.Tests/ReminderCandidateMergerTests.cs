using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class ReminderCandidateMergerTests
{
    private static readonly DateTimeOffset ReferenceTime =
        new(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Merge_UnifiesOcrAndModelCandidatesForTheSameInstant()
    {
        var ocr = CreateCandidate(
            "2026.9.11 16:30",
            "2026-09-11T16:30:00",
            "申报截止时间：2026.9.11（周五）16:30",
            "Ocr");
        var model = CreateCandidate(
            "2026.9.11",
            null,
            "申报截止时间：2026.9.11（周五）16:30",
            "Model") with
        {
            AmbiguityReason = "ModelOnlyInterpretation",
        };

        var result = new ReminderCandidateMerger().Merge(
            [ocr],
            [model],
            ReferenceTime,
            "China Standard Time",
            ["zh-Hans"]);

        var candidate = Assert.Single(result);
        Assert.Equal("2026-09-11T16:30:00", candidate.NormalizedValue);
        Assert.Equal("Ocr", candidate.Source);
    }

    [Fact]
    public void Merge_UnifiesEquivalentNormalizedPrecisionForTheSameInstant()
    {
        var ocr = CreateCandidate(
            "2026.9.11 16:30",
            "2026-09-11T16:30:00",
            "申报截止时间：2026.9.11 16:30",
            "Ocr");
        var model = CreateCandidate(
            "2026.9.11 16:30:00",
            "2026-09-11T16:30:00.0000000",
            "申报截止时间：2026.9.11 16:30",
            "Model");

        var result = new ReminderCandidateMerger().Merge(
            [ocr],
            [model],
            ReferenceTime,
            "China Standard Time",
            ["zh-Hans"]);

        Assert.Equal("Ocr", Assert.Single(result).Source);
    }

    [Fact]
    public void Merge_KeepsDifferentInstantsAsSeparateReminders()
    {
        var first = CreateCandidate(
            "第一场 2026.9.11 16:30",
            "2026-09-11T16:30:00",
            "第一场 2026.9.11 16:30",
            "Ocr");
        var second = CreateCandidate(
            "第二场 2026.9.11 18:30",
            "2026-09-11T18:30:00",
            "第二场 2026.9.11 18:30",
            "Model");

        var result = new ReminderCandidateMerger().Merge(
            [first],
            [second],
            ReferenceTime,
            "China Standard Time",
            ["zh-Hans"]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, candidate =>
            candidate.NormalizedValue == "2026-09-11T16:30:00");
        Assert.Contains(result, candidate =>
            candidate.NormalizedValue == "2026-09-11T18:30:00");
    }

    [Fact]
    public void Merge_FullDateTimeSubsumesRelatedDateOnlyFragment()
    {
        var date = CreateCandidate(
            "2026.9.11",
            "2026-09-11",
            "申报截止时间：2026.9.11",
            "Ocr");
        var full = CreateCandidate(
            "2026.9.11（周五）16:30",
            "2026-09-11T16:30:00",
            "申报截止时间：2026.9.11（周五）16:30",
            "Model");

        var result = new ReminderCandidateMerger().Merge(
            [date],
            [full],
            ReferenceTime,
            "China Standard Time",
            ["zh-Hans"]);

        Assert.Equal(
            "2026-09-11T16:30:00",
            Assert.Single(result).NormalizedValue);
    }

    [Fact]
    public void Merge_DoesNotCollapseUnrelatedEventsThatShareOnlyADate()
    {
        var date = CreateCandidate(
            "2026.9.11",
            "2026-09-11",
            "校庆日期：2026.9.11",
            "Ocr");
        var full = CreateCandidate(
            "2026.9.11 16:30",
            "2026-09-11T16:30:00",
            "申报截止时间：2026.9.11 16:30",
            "Model");

        var result = new ReminderCandidateMerger().Merge(
            [date],
            [full],
            ReferenceTime,
            "China Standard Time",
            ["zh-Hans"]);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Merge_ExplicitYearSubsumesContainedMissingYearInterpretation()
    {
        const string evidence = "帖子发布于 2026/6/30 17:05";
        var explicitTimestamp = CreateCandidate(
            "2026/6/30 17:05",
            "2026-06-30T17:05:00",
            evidence,
            "Ocr");
        var missingYear = CreateCandidate(
            "6/30 17:05",
            "2027-06-30T17:05:00",
            evidence,
            "Ocr") with
        {
            AmbiguityReason = "MissingYear",
        };

        var result = new ReminderCandidateMerger().Merge(
            [explicitTimestamp, missingYear],
            [],
            ReferenceTime,
            "China Standard Time",
            ["zh-Hans"]);

        var candidate = Assert.Single(result);
        Assert.Equal("2026-06-30T17:05:00", candidate.NormalizedValue);
        Assert.Null(candidate.AmbiguityReason);
    }

    [Fact]
    public void Merge_ModelMissingYearUsesReferenceYearInsteadOfModelGuess()
    {
        var model = CreateCandidate(
            "6月30日 17:05",
            "2027-06-30T17:05:00",
            "活动日期 6月30日 17:05",
            "Model") with
        {
            AmbiguityReason = "ModelOnlyInterpretation",
        };

        var result = new ReminderCandidateMerger().Merge(
            [],
            [model],
            ReferenceTime,
            "China Standard Time",
            ["zh-Hans"]);

        var candidate = Assert.Single(result);
        Assert.Equal("2026-06-30T17:05:00", candidate.NormalizedValue);
        Assert.Equal("MissingYear", candidate.AmbiguityReason);
    }

    [Fact]
    public void Merge_RemoteVisionKeepsNoLocalOcrEvidenceDisclosure()
    {
        var model = CreateCandidate(
            "6月30日 17:05",
            "2027-06-30T17:05:00",
            "图片中显示活动日期 6月30日 17:05",
            "Model") with
        {
            AmbiguityReason = "RemoteVisionNoLocalOcrEvidence",
            BoundingBox = null,
        };

        var result = new ReminderCandidateMerger().Merge(
            [],
            [model],
            ReferenceTime,
            "China Standard Time",
            ["zh-Hans"]);

        var candidate = Assert.Single(result);
        Assert.Equal("2026-06-30T17:05:00", candidate.NormalizedValue);
        Assert.Equal("RemoteVisionNoLocalOcrEvidence", candidate.AmbiguityReason);
        Assert.Null(candidate.BoundingBox);
    }

    [Fact]
    public void Merge_ModelNormalizationCannotContradictItsExplicitEvidence()
    {
        var model = CreateCandidate(
            "2026年9月11日 16:30",
            "2027-09-11T16:30:00",
            "申报截止时间：2026年9月11日 16:30",
            "Model") with
        {
            AmbiguityReason = "ModelOnlyInterpretation",
        };

        var result = new ReminderCandidateMerger().Merge(
            [],
            [model],
            ReferenceTime,
            "China Standard Time",
            ["zh-Hans"]);

        var candidate = Assert.Single(result);
        Assert.Equal("2026-09-11T16:30:00", candidate.NormalizedValue);
        Assert.Equal("ModelOnlyInterpretation", candidate.AmbiguityReason);
    }

    private static EntityCandidateDraft CreateCandidate(
        string rawText,
        string? normalizedValue,
        string evidence,
        string source) =>
        new(
            "DateTime",
            rawText,
            normalizedValue,
            evidence,
            source)
        {
            ReferenceTimeUtc = ReferenceTime,
            TimeZoneId = "China Standard Time",
        };
}
