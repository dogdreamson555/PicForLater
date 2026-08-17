using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class DeterministicEntityExtractorTests
{
    [Fact]
    public void Extract_FindsMultipleChineseDatesAndAddressWithAuditableEvidence()
    {
        var firstBox = new OcrBoundingBox(0.1, 0.2, 0.8, 0.1);
        var document = CreateDocument(
            new OcrLine(
                "新PV日期：2026年9月12日 Nintendo 专场",
                firstBox,
                [],
                0.99),
            new OcrLine(
                "发售日期：2026年11月19日",
                new OcrBoundingBox(0.1, 0.3, 0.8, 0.1),
                [],
                0.99),
            new OcrLine(
                "线下体验：上海市浦东新区世纪大道100号",
                new OcrBoundingBox(0.1, 0.4, 0.8, 0.1),
                [],
                0.98));

        var result = new DeterministicEntityExtractor().Extract(
            document,
            new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero),
            "China Standard Time");

        var dates = result.Candidates.Where(candidate => candidate.Kind == "DateTime").ToArray();
        Assert.Equal(2, dates.Length);
        Assert.Contains(dates, candidate => candidate.NormalizedValue == "2026-09-12");
        Assert.Contains(dates, candidate => candidate.NormalizedValue == "2026-11-19");
        Assert.Equal(firstBox, dates[0].BoundingBox);
        Assert.All(dates, candidate => Assert.Equal("Ocr", candidate.Source));
        var location = Assert.Single(
            result.Candidates,
            candidate => candidate.Kind == "Location");
        Assert.Contains("世纪大道100号", location.RawText, StringComparison.Ordinal);
        Assert.Equal("China Standard Time", location.TimeZoneId);
        Assert.Equal("local.deterministic-entities", result.Provenance.ProviderId);
        Assert.Equal(AnalysisExecutionLocation.Local, result.Provenance.ExecutionLocation);
        Assert.Equal(AnalysisOutputKind.DeterministicEntityCandidates, result.Provenance.OutputKind);
    }

    [Fact]
    public void Extract_PreservesAmbiguousSlashDateWithoutGuessingOrder()
    {
        var document = CreateDocument(new OcrLine(
            "Event date 03/04/2027",
            new OcrBoundingBox(0, 0, 1, 0.1),
            [],
            0.9));

        var candidate = Assert.Single(
            new DeterministicEntityExtractor().Extract(
                document,
                DateTimeOffset.UtcNow,
                "UTC").Candidates);

        Assert.Equal("DateTime", candidate.Kind);
        Assert.Null(candidate.NormalizedValue);
        Assert.Equal("DateOrder", candidate.AmbiguityReason);
        Assert.Equal("Event date 03/04/2027", candidate.Evidence);
    }

    [Fact]
    public void Extract_RejectsInvalidCalendarValues()
    {
        var document = CreateDocument(new OcrLine(
            "发布日期 2026年13月42日",
            new OcrBoundingBox(0, 0, 1, 0.1),
            [],
            0.7));

        var result = new DeterministicEntityExtractor().Extract(
            document,
            DateTimeOffset.UtcNow,
            "UTC");

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Extract_MissingYearUsesReferenceYearInConfirmedTimeZone()
    {
        var document = CreateDocument(new OcrLine(
            "活动日期 13/1",
            new OcrBoundingBox(0, 0, 1, 0.1),
            [],
            0.9));

        var candidate = Assert.Single(
            new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 12, 31, 16, 30, 0, TimeSpan.Zero),
                "China Standard Time").Candidates);

        Assert.Equal("2027-01-13", candidate.NormalizedValue);
        Assert.Equal("MissingYear", candidate.AmbiguityReason);
    }

    [Fact]
    public void Extract_MissingYearDoesNotRollAnElapsedMonthIntoNextYear()
    {
        var document = CreateDocument(new OcrLine(
            "活动日期 6月30日 17:05",
            new OcrBoundingBox(0, 0, 1, 0.1),
            [],
            0.9));

        var candidate = Assert.Single(
            new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero),
                "China Standard Time").Candidates);

        Assert.Equal("2026-06-30T17:05:00", candidate.NormalizedValue);
        Assert.Equal("MissingYear", candidate.AmbiguityReason);
    }

    [Fact]
    public void Extract_ResolvesRelativeChineseTimeAndContextualVenueFromReferenceTime()
    {
        var lineBox = new OcrBoundingBox(0.1, 0.2, 0.8, 0.2);
        var document = CreateDocument(new OcrLine(
            "今晚7点50分来会议室一趟",
            lineBox,
            [],
            0.99));

        var candidates = new DeterministicEntityExtractor().Extract(
            document,
            new DateTimeOffset(2026, 7, 29, 2, 0, 0, TimeSpan.Zero),
            "China Standard Time").Candidates;

        var date = Assert.Single(candidates, candidate => candidate.Kind == "DateTime");
        Assert.Equal("2026-07-29T19:50:00", date.NormalizedValue);
        Assert.Equal("RelativeDate", date.AmbiguityReason);
        Assert.Equal(lineBox, date.BoundingBox);
        var location = Assert.Single(candidates, candidate => candidate.Kind == "Location");
        Assert.Equal("会议室", location.RawText);
        Assert.Equal("会议室", location.NormalizedValue);
    }

    [Fact]
    public void Extract_ResolvesRelativeChineseChatMessageWithTrailingClause()
    {
        var document = CreateDocument(new OcrLine(
            "今晚7点50分来会议室一趟，我有事找你",
            new OcrBoundingBox(0.1, 0.2, 0.8, 0.2),
            [],
            0.99));

        var candidates = new DeterministicEntityExtractor().Extract(
            document,
            new DateTimeOffset(2026, 7, 30, 2, 0, 0, TimeSpan.Zero),
            "China Standard Time").Candidates;

        Assert.Contains(
            candidates,
            candidate => candidate.Kind == "DateTime"
                         && candidate.NormalizedValue == "2026-07-30T19:50:00");
        Assert.Contains(
            candidates,
            candidate => candidate.Kind == "Location"
                         && candidate.NormalizedValue == "会议室");
    }

    [Fact]
    public void Extract_RoutesUndeterminedScriptTagsReturnedByMultilingualOcr()
    {
        var line = new OcrLine(
            "今晚7点50分来会议室一趟，我有事找你",
            new OcrBoundingBox(0.1, 0.2, 0.8, 0.2),
            [],
            0.99);
        var baseDocument = CreateDocument(line);
        var document = baseDocument with
        {
            LanguageTags = ["und-Hani", "und-Latn"],
        };

        var candidates = new DeterministicEntityExtractor().Extract(
            document,
            new DateTimeOffset(2026, 7, 30, 2, 0, 0, TimeSpan.Zero),
            "China Standard Time").Candidates;

        Assert.Contains(
            candidates,
            candidate => candidate.Kind == "DateTime"
                         && candidate.NormalizedValue == "2026-07-30T19:50:00");
    }

    [Fact]
    public void Extract_CombinesExplicitDateAndTimeSeparatedByAWeekdayAnnotation()
    {
        var document = CreateDocument(new OcrLine(
            "申报截止时间：2026.9.11(周五)16:30",
            new OcrBoundingBox(0.1, 0.2, 0.8, 0.2),
            [],
            0.99));

        var candidates = new DeterministicEntityExtractor().Extract(
            document,
            new DateTimeOffset(2026, 7, 30, 2, 0, 0, TimeSpan.Zero),
            "China Standard Time").Candidates;

        Assert.Contains(
            candidates,
            candidate => candidate.Kind == "DateTime"
                         && candidate.NormalizedValue == "2026-09-11T16:30:00");
    }

    [Fact]
    public void Extract_CombinesDateAndTimeSplitAcrossAdjacentOcrLines()
    {
        var document = CreateDocument(
            new OcrLine(
                "申报截止时间：2026.9.11（周",
                new OcrBoundingBox(0.08, 0.2, 0.75, 0.08),
                [],
                0.99),
            new OcrLine(
                "五）16:30",
                new OcrBoundingBox(0.08, 0.31, 0.28, 0.08),
                [],
                0.99));

        var dateTimes = new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 7, 30, 2, 0, 0, TimeSpan.Zero),
                "China Standard Time")
            .Candidates
            .Where(candidate => candidate.Kind == "DateTime")
            .ToArray();

        var candidate = Assert.Single(dateTimes);
        Assert.Equal("2026-09-11T16:30:00", candidate.NormalizedValue);
        Assert.Contains("2026.9.11", candidate.RawText, StringComparison.Ordinal);
        Assert.Contains("16:30", candidate.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_DoesNotCombineDateAndTimeFromDistantOcrLines()
    {
        var document = CreateDocument(
            new OcrLine(
                "发布日期：2026.9.11",
                new OcrBoundingBox(0.08, 0.1, 0.6, 0.05),
                [],
                0.99),
            new OcrLine(
                "另一活动 16:30",
                new OcrBoundingBox(0.08, 0.8, 0.6, 0.05),
                [],
                0.99));

        var dateTimes = new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 7, 30, 2, 0, 0, TimeSpan.Zero),
                "China Standard Time")
            .Candidates
            .Where(candidate => candidate.Kind == "DateTime")
            .ToArray();

        Assert.DoesNotContain(
            dateTimes,
            candidate => candidate.NormalizedValue == "2026-09-11T16:30:00");
    }

    [Fact]
    public void Extract_DoesNotCombineAdjacentDateAndTimeFromSeparateColumns()
    {
        var document = CreateDocument(
            new OcrLine(
                "发布日期：2026.9.11",
                new OcrBoundingBox(0.05, 0.2, 0.45, 0.08),
                [],
                0.99),
            new OcrLine(
                "另一场 16:30",
                new OcrBoundingBox(0.35, 0.29, 0.45, 0.08),
                [],
                0.99));

        var dateTimes = new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 7, 30, 2, 0, 0, TimeSpan.Zero),
                "China Standard Time")
            .Candidates
            .Where(candidate => candidate.Kind == "DateTime")
            .ToArray();

        Assert.DoesNotContain(
            dateTimes,
            candidate => candidate.NormalizedValue == "2026-09-11T16:30:00");
    }

    [Fact]
    public void Extract_DoesNotCombineDateAndTimeAcrossAnEventBoundary()
    {
        var document = CreateDocument(new OcrLine(
            "首场日期2026.9.11，另一场16:30",
            new OcrBoundingBox(0.1, 0.2, 0.8, 0.2),
            [],
            0.99));

        var candidates = new DeterministicEntityExtractor().Extract(
            document,
            new DateTimeOffset(2026, 7, 30, 2, 0, 0, TimeSpan.Zero),
            "China Standard Time").Candidates;

        Assert.DoesNotContain(
            candidates,
            candidate => candidate.NormalizedValue == "2026-09-11T16:30:00");
    }

    [Fact]
    public void Extract_HandlesWhitespaceIntroducedByRealOcrWithoutScenarioSpecificRules()
    {
        var document = CreateDocument(new OcrLine(
            "今 晚 7 点 50 分 来 会 议 室 一 趟",
            new OcrBoundingBox(0.1, 0.2, 0.8, 0.2),
            [],
            0.95));

        var candidates = new DeterministicEntityExtractor().Extract(
            document,
            new DateTimeOffset(2026, 7, 29, 2, 0, 0, TimeSpan.Zero),
            "China Standard Time").Candidates;

        Assert.Contains(
            candidates,
            candidate => candidate.Kind == "DateTime"
                         && candidate.NormalizedValue == "2026-07-29T19:50:00");
        Assert.Contains(
            candidates,
            candidate => candidate.Kind == "Location"
                         && candidate.NormalizedValue == "会议室");
    }

    [Fact]
    public void Extract_RoutesMixedLanguageDocumentByLineScriptWithoutFalseDateOrder()
    {
        var document = CreateDocument(new OcrLine(
            "2026年08月29日 12:00",
            new OcrBoundingBox(0.1, 0.2, 0.8, 0.2),
            [],
            0.95));

        var candidate = Assert.Single(
            new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 7, 29, 2, 0, 0, TimeSpan.Zero),
                "China Standard Time").Candidates,
            item => item.Kind == "DateTime");

        Assert.Null(candidate.NormalizedValue);
        Assert.Equal("TimeOfDay", candidate.AmbiguityReason);
    }

    [Theory]
    [InlineData("tomorrow at 7:50 PM", "en", "2026-07-30T19:50:00")]
    [InlineData("reunión mañana a las 19:50", "es", "2026-07-30T19:50:00")]
    [InlineData("réunion demain à 19h50", "fr", "2026-07-30T19:50:00")]
    [InlineData("reunião amanhã às 19:50", "pt", "2026-07-30T19:50:00")]
    [InlineData("Treffen morgen um 19:50", "de", "2026-07-30T19:50:00")]
    [InlineData("riunione domani alle 19:50", "it", "2026-07-30T19:50:00")]
    [InlineData("toplantı yarın saat 19:50", "tr", "2026-07-30T19:50:00")]
    public void Extract_RecognizesOfficiallySupportedRelativeDateTimeLanguages(
        string text,
        string languageTag,
        string expected)
    {
        var document = new OcrDocument(
            text,
            [
                new OcrLine(
                    text,
                    new OcrBoundingBox(0, 0, 1, 0.1),
                    [],
                    0.95),
            ],
            [languageTag],
            [],
            new AnalysisProvenance(
                "test.ocr",
                null,
                null,
                new Dictionary<string, string>(),
                "test.v1"),
            1000,
            1000);

        var candidate = Assert.Single(
            new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero),
                "UTC").Candidates,
            item => item.Kind == "DateTime");

        Assert.Equal(expected, candidate.NormalizedValue);
        Assert.Equal("RelativeDate", candidate.AmbiguityReason);
    }

    [Fact]
    public void Extract_ChineseMonthAndDayWithoutYearRemainsVisibleForConfirmation()
    {
        var document = CreateDocument(new OcrLine(
            "活动安排在9月15日下午3点",
            new OcrBoundingBox(0, 0, 1, 0.1),
            [],
            0.95));

        var candidate = Assert.Single(
            new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero),
                "China Standard Time").Candidates,
            item => item.Kind == "DateTime");

        Assert.Equal("2026-09-15T15:00:00", candidate.NormalizedValue);
        Assert.Equal("MissingYear", candidate.AmbiguityReason);
    }

    [Fact]
    public void Extract_ChineseYearAndMonthPreservesPartialDateForConfirmation()
    {
        var document = CreateDocument(new OcrLine(
            "年度总决赛&颁奖日期（预计）：2027年5月",
            new OcrBoundingBox(0, 0, 1, 0.1),
            [],
            0.95));

        var candidate = Assert.Single(
            new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero),
                "China Standard Time").Candidates,
            item => item.Kind == "DateTime");

        Assert.Equal("2027年5月", candidate.RawText);
        Assert.Equal("2027-05", candidate.NormalizedValue);
        Assert.Equal("MissingDay", candidate.AmbiguityReason);
    }

    [Fact]
    public void Extract_ChineseYearPreservesPartialDateForConfirmation()
    {
        var document = CreateDocument(new OcrLine(
            "预计年份：2027年",
            new OcrBoundingBox(0, 0, 1, 0.1),
            [],
            0.95));

        var candidate = Assert.Single(
            new DeterministicEntityExtractor().Extract(
                document,
                new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero),
                "China Standard Time").Candidates,
            item => item.Kind == "DateTime");

        Assert.Equal("2027年", candidate.RawText);
        Assert.Equal("2027", candidate.NormalizedValue);
        Assert.Equal("MissingMonthAndDay", candidate.AmbiguityReason);
    }

    [Fact]
    public void Extract_UnsupportedLanguageDoesNotGuessAndReportsCapabilityWarning()
    {
        const string text = "会議は明日の午後7時50分";
        var document = new OcrDocument(
            text,
            [
                new OcrLine(
                    text,
                    new OcrBoundingBox(0, 0, 1, 0.1),
                    [],
                    0.95),
            ],
            ["ja"],
            [],
            new AnalysisProvenance(
                "test.ocr",
                null,
                null,
                new Dictionary<string, string>(),
                "test.v1"),
            1000,
            1000);

        var result = new DeterministicEntityExtractor().Extract(
            document,
            new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero),
            "Tokyo Standard Time");

        Assert.Empty(result.Candidates);
        Assert.Contains("datetime-recognizer-language-unsupported", result.Warnings);
    }

    private static OcrDocument CreateDocument(params OcrLine[] lines) => new(
        string.Join(Environment.NewLine, lines.Select(line => line.Text)),
        lines,
        ["zh-Hans", "en"],
        [],
        new AnalysisProvenance(
            "test.ocr",
            null,
            null,
            new Dictionary<string, string>(),
            "test.v1"),
        1000,
        1000);
}
