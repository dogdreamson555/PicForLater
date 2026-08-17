using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class QwenStructuredOutputParserTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AnalysisProvenance Provenance = new(
        "local.qwen3-vl",
        "qwen3-vl-2b-instruct",
        "1",
        new Dictionary<string, string> { ["model.onnx"] = new string('a', 64) },
        QwenStructuredOutputParser.SchemaVersion);

    [Fact]
    public void ValidOutput_PreservesOnlyExistingCategoriesAndAuditableEvidence()
    {
        var categoryId = Guid.NewGuid();
        var parser = new QwenStructuredOutputParser();
        var result = parser.Parse(
            CreateOutput([categoryId.ToString("D")], "7月20日"),
            CreateDocument("活动时间 7月20日 14:30"),
            new AnalysisCompositionContext([new AnalysisCategoryOption(categoryId, "活动")]),
            Provenance,
            new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero),
            "China Standard Time");

        Assert.Equal("活动海报", result.Draft.Title);
        Assert.Equal(categoryId, Assert.Single(result.Draft.SuggestedCategoryIds));
        var entity = Assert.Single(result.Draft.EntityCandidates);
        Assert.Equal("7月20日", entity.Evidence);
        Assert.Equal("DateTime", entity.Kind);
        Assert.Equal("Model", entity.Source);
        Assert.Equal("China Standard Time", entity.TimeZoneId);
        Assert.Equal("ModelInterpretation", entity.AmbiguityReason);
    }

    [Fact]
    public void UnknownCategoryId_IsIgnoredWithoutDiscardingDraft()
    {
        var result = new QwenStructuredOutputParser().Parse(
            CreateOutput([Guid.NewGuid().ToString("D")], "7月20日"),
            CreateDocument("7月20日"),
            new AnalysisCompositionContext([]),
            Provenance);

        Assert.Equal("活动海报", result.Draft.Title);
        Assert.Empty(result.Draft.SuggestedCategoryIds);
        Assert.Contains("qwen.invalid-category-id-ignored", result.Warnings);
    }

    [Fact]
    public void EntityVisibleOnlyToModel_IsPreservedAsLowerTrustCandidate()
    {
        var result = new QwenStructuredOutputParser().Parse(
            CreateOutput([], "海报写着明天", visualFacts: ["海报写着明天"]),
            CreateDocument(string.Empty),
            new AnalysisCompositionContext([]),
            Provenance);

        var entity = Assert.Single(result.Draft.EntityCandidates);
        Assert.Equal("Model", entity.Source);
        Assert.Equal("ModelOnlyInterpretation", entity.AmbiguityReason);
        Assert.Equal("海报写着明天", entity.Evidence);
        Assert.Contains("qwen.entity-not-corroborated-by-ocr", result.Warnings);
    }

    [Fact]
    public void EntityWithoutAnyQuotedEvidence_IsIgnoredWithoutDiscardingDraft()
    {
        var result = new QwenStructuredOutputParser().Parse(
            CreateOutput([], string.Empty),
            CreateDocument(string.Empty),
            new AnalysisCompositionContext([]),
            Provenance);

        Assert.Empty(result.Draft.EntityCandidates);
        Assert.Contains("qwen.invalid-entity-evidence-ignored", result.Warnings);
    }

    [Fact]
    public void PartialYearMonthEntity_IsPreservedForEditableConfirmation()
    {
        var output = CreateOutput([], "2027年5月").Replace(
            "\"normalizedValue\":\"2026-07-20\"",
            "\"normalizedValue\":\"2027-05\"",
            StringComparison.Ordinal);

        var result = new QwenStructuredOutputParser().Parse(
            output,
            CreateDocument("颁奖日期（预计）：2027年5月"),
            new AnalysisCompositionContext([]),
            Provenance);

        var entity = Assert.Single(result.Draft.EntityCandidates);
        Assert.Equal("2027-05", entity.NormalizedValue);
        Assert.Equal("ModelInterpretation", entity.AmbiguityReason);
    }

    [Fact]
    public void SchemaVersionMismatch_IsRejected()
    {
        var output = JsonSerializer.Serialize(new
        {
            schemaVersion = "future.v2",
            visualFacts = Array.Empty<string>(),
            title = "Title",
            summary = string.Empty,
            categoryIds = Array.Empty<string>(),
            entities = Array.Empty<object>(),
            detectedLanguages = new[] { "en" },
            warnings = Array.Empty<string>(),
        }, JsonOptions);

        var exception = Assert.Throws<QwenStructuredOutputException>(() =>
            new QwenStructuredOutputParser().Parse(
                output,
                CreateDocument("Title"),
                new AnalysisCompositionContext([]),
                Provenance));

        Assert.Equal("qwen.schema-version-mismatch", exception.ErrorCode);
    }

    [Fact]
    public void AdditionalProperties_AreRejectedEvenIfTheDeserializerCouldIgnoreThem()
    {
        var output = CreateOutput([], "7月20日").Replace(
            "\"warnings\":[]",
            "\"warnings\":[],\"toolCalls\":[\"not-allowed\"]",
            StringComparison.Ordinal);

        var exception = Assert.Throws<QwenStructuredOutputException>(() =>
            new QwenStructuredOutputParser().Parse(
                output,
                CreateDocument("7月20日"),
                new AnalysisCompositionContext([]),
                Provenance));

        Assert.Equal("qwen.schema-validation-failed", exception.ErrorCode);
    }

    [Fact]
    public void OneExcessVisualFact_IsBoundedAndRecordsNormalizationWarning()
    {
        var output = CreateOutput(
            [],
            "海报写着明天",
            visualFacts: ["事实一", "事实二", "事实三", "事实四"]);

        var normalized = QwenStructuredOutputParser.NormalizeGeneratedOutput(output);
        var result = new QwenStructuredOutputParser().Parse(
            normalized,
            CreateDocument("海报写着明天"),
            new AnalysisCompositionContext([]),
            Provenance);

        Assert.Equal(["事实一", "事实二", "事实三"], result.VisualFacts);
        Assert.Contains(
            QwenStructuredOutputParser.VisualFactsTruncatedWarning,
            result.Warnings);
    }

    [Fact]
    public void MultipleExcessVisualFacts_RemainStrictlyRejected()
    {
        var output = CreateOutput(
            [],
            "海报写着明天",
            visualFacts: ["事实一", "事实二", "事实三", "事实四", "事实五"]);

        var exception = Assert.Throws<QwenStructuredOutputException>(() =>
            QwenStructuredOutputParser.NormalizeGeneratedOutput(output));

        Assert.Equal("qwen.schema-validation-failed", exception.ErrorCode);
    }

    [Fact]
    public void NonBcp47LanguageTag_IsIgnoredAndOcrLanguageIsPreserved()
    {
        var output = CreateOutput([], "7月20日").Replace("zh-Hans", "zh_Hans", StringComparison.Ordinal);

        var result = new QwenStructuredOutputParser().Parse(
            output,
            CreateDocument("7月20日"),
            new AnalysisCompositionContext([]),
            Provenance);

        Assert.Equal(["zh-Hans"], result.LanguageTags);
        Assert.Contains("qwen.invalid-language-tag-ignored", result.Warnings);
    }

    [Fact]
    public void CompleteJsonFraming_AcceptsWhitespaceAndBracesInsideStrings()
    {
        var framed = QwenStructuredOutputParser.TryExtractCompleteJsonObject(
            " \r\n{\"summary\":\"escaped \\\"}\\\" and { text\",\"items\":[{}]}\t ",
            out var json);

        Assert.True(framed);
        Assert.Equal("{\"summary\":\"escaped \\\"}\\\" and { text\",\"items\":[{}]}", json);
    }

    [Theory]
    [InlineData("{\"title\":\"partial\"")]
    [InlineData("{\"title\":\"done\"} trailing")]
    [InlineData("[\"not-an-object\"]")]
    public void CompleteJsonFraming_RejectsIncompleteOrNonObjectOutput(string value)
    {
        Assert.False(QwenStructuredOutputParser.TryExtractCompleteJsonObject(value, out _));
    }

    [Fact]
    public void CompactGenerationOutput_IsExpandedToTheStrictInternalSchema()
    {
        var generated = JsonSerializer.Serialize(new
        {
            schemaVersion = QwenStructuredOutputParser.SchemaVersion,
            title = "图片标题",
            summary = "图片简介",
            visualFacts = new[] { "网页截图" },
            detectedLanguages = new[] { "zh-Hans" },
            warnings = Array.Empty<string>(),
        }, JsonOptions);

        var normalized = QwenStructuredOutputParser.NormalizeGeneratedOutput(generated);
        var result = new QwenStructuredOutputParser().Parse(
            normalized,
            CreateDocument("OCR facts"),
            new AnalysisCompositionContext([]),
            Provenance);

        Assert.Equal("图片标题", result.Draft.Title);
        Assert.Equal("图片简介", result.Draft.Summary);
        Assert.Equal("网页截图", Assert.Single(result.VisualFacts));
        Assert.Empty(result.Draft.SuggestedCategoryIds);
        Assert.Empty(result.Draft.EntityCandidates);
    }

    [Fact]
    public void GenerationSchema_DisablesFlexibleWhitespaceLoop()
    {
        using var document = JsonDocument.Parse(QwenStructuredOutputParser.JsonSchema);
        var guidance = document.RootElement.GetProperty("x-guidance");

        Assert.False(guidance.GetProperty("whitespace_flexible").GetBoolean());
        Assert.Equal(",", guidance.GetProperty("item_separator").GetString());
    }

    [Fact]
    public void GenerationSchema_RequiresAuditableEntityCandidates()
    {
        using var document = JsonDocument.Parse(QwenStructuredOutputParser.JsonSchema);
        var root = document.RootElement;
        var required = root.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("entities", required);
        Assert.Contains("categoryIds", required);
        var entities = root.GetProperty("properties").GetProperty("entities");
        Assert.Equal(3, entities.GetProperty("maxItems").GetInt32());
        Assert.False(entities.GetProperty("items").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void RepeatedNumericDegeneration_IsRejected()
    {
        var output = CreateDraftOutput(
            ". 200 22000 22022077500 00 00 220 20 20 20 20 20 2000 00 00 00",
            string.Join(' ', Enumerable.Repeat("00", 24)));

        var exception = Assert.Throws<QwenStructuredOutputException>(() =>
            new QwenStructuredOutputParser().Parse(
                output,
                CreateDocument("thingsaboutwebdev 03/16/26"),
                new AnalysisCompositionContext([]),
                Provenance));

        Assert.Equal("qwen.degenerate-text-output", exception.ErrorCode);
    }

    [Fact]
    public void NumericFactMissingFromOcrEvidence_IsRejected()
    {
        var exception = Assert.Throws<QwenStructuredOutputException>(() =>
            new QwenStructuredOutputParser().Parse(
                CreateDraftOutput("活动 999999", "活动安排"),
                CreateDocument("活动时间 7月20日"),
                new AnalysisCompositionContext([]),
                Provenance));

        Assert.Equal("qwen.ungrounded-numeric-output", exception.ErrorCode);
    }

    [Fact]
    public void RemoteDirectImage_DoesNotPretendEmptyOcrCanGroundVisibleImageText()
    {
        var provenance = Provenance with
        {
            ExecutionLocation = AnalysisExecutionLocation.RemoteApi,
            RemoteInputMode = RemoteInputMode.DirectImage,
        };
        var skippedDocument = CreateDocument(string.Empty) with
        {
            Provenance = provenance with
            {
                OutputKind = AnalysisOutputKind.OcrFacts,
                StageOutcome = AnalysisStageOutcome.SkippedByRemoteDirectImage,
            },
        };

        var result = new QwenStructuredOutputParser().Parse(
            CreateOutput([], "7月20日"),
            skippedDocument,
            new AnalysisCompositionContext([]),
            provenance);

        var entity = Assert.Single(result.Draft.EntityCandidates);
        Assert.Equal("RemoteVisionNoLocalOcrEvidence", entity.AmbiguityReason);
        Assert.Null(entity.BoundingBox);
        Assert.DoesNotContain("qwen.entity-not-corroborated-by-ocr", result.Warnings);
    }

    [Fact]
    public void SchemaBoundSummary_IsShortenedToItsFirstCompleteSentence()
    {
        var longSummary =
            "SKILLS is a framework for managing agent skills. "
            + string.Join(' ', Enumerable.Repeat("Additional grounded context", 8))
            + ",";
        var result = new QwenStructuredOutputParser().Parse(
            CreateDraftOutput("Agent skills", longSummary),
            CreateDocument("Agent skills Additional grounded context"),
            new AnalysisCompositionContext([]),
            Provenance);

        Assert.Equal(
            "SKILLS is a framework for managing agent skills.",
            result.Draft.Summary);
        Assert.Contains(
            "qwen.summary-shortened-to-complete-sentence",
            result.Warnings);
    }

    [Fact]
    public void LongSummaryWithoutSentenceEnd_IsClosedAtItsFirstIndependentClause()
    {
        var longSummary =
            "The directory organizes reusable agent skills for local development, "
            + string.Join(' ', Enumerable.Repeat("including additional tools", 8));
        var result = new QwenStructuredOutputParser().Parse(
            CreateDraftOutput("Agent skills", longSummary),
            CreateDocument("Agent skills directory tools"),
            new AnalysisCompositionContext([]),
            Provenance);

        Assert.Equal(
            "The directory organizes reusable agent skills for local development.",
            result.Draft.Summary);
        Assert.Contains(
            "qwen.summary-shortened-to-complete-sentence",
            result.Warnings);
    }

    private static string CreateOutput(
        string[] categoryIds,
        string evidence,
        string[]? visualFacts = null) => JsonSerializer.Serialize(new
        {
            schemaVersion = QwenStructuredOutputParser.SchemaVersion,
            visualFacts = visualFacts ?? ["一张活动海报"],
            title = "活动海报",
            summary = "活动安排",
            categoryIds,
            entities = new[]
            {
                new
                {
                    kind = "date",
                    rawText = evidence,
                    normalizedValue = "2026-07-20",
                    evidence,
                },
            },
            detectedLanguages = new[] { "zh-Hans" },
            warnings = Array.Empty<string>(),
        }, JsonOptions);

    private static string CreateDraftOutput(string title, string summary) => JsonSerializer.Serialize(new
    {
        schemaVersion = QwenStructuredOutputParser.SchemaVersion,
        visualFacts = Array.Empty<string>(),
        title,
        summary,
        categoryIds = Array.Empty<string>(),
        entities = Array.Empty<object>(),
        detectedLanguages = new[] { "zh-Hans" },
        warnings = Array.Empty<string>(),
    }, JsonOptions);

    private static OcrDocument CreateDocument(string text) => new(
        text,
        [],
        ["zh-Hans"],
        [],
        Provenance,
        800,
        600);
}
