using System.Globalization;
using System.Text;
using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

public sealed class QwenStructuredOutputParser
{
    public const string SchemaVersion = "picforlater.analysis.v1";
    public const int MaximumOutputCharacters = 65_536;
    public const string VisualFactsTruncatedWarning =
        "qwen.visual-facts-truncated-to-schema";

    public static string JsonSchema { get; } =
        """
        {
          "x-guidance":{"whitespace_flexible":false,"item_separator":","},
          "type":"object",
          "additionalProperties":false,
          "required":["schemaVersion","title","summary","visualFacts","categoryIds","entities","detectedLanguages","warnings"],
          "properties":{
            "schemaVersion":{"const":"picforlater.analysis.v1"},
            "title":{"type":"string","maxLength":80},
            "summary":{"type":"string","maxLength":320},
            "visualFacts":{"type":"array","maxItems":3,"items":{"type":"string","maxLength":120}},
            "categoryIds":{"type":"array","maxItems":8,"items":{"type":"string","maxLength":36}},
            "entities":{
              "type":"array",
              "maxItems":3,
              "items":{
                "type":"object",
                "additionalProperties":false,
                "required":["kind","rawText","normalizedValue","evidence"],
                "properties":{
                  "kind":{"enum":["date","time","datetime","location","address"]},
                  "rawText":{"type":"string","maxLength":80},
                  "normalizedValue":{"type":["string","null"],"maxLength":120},
                  "evidence":{"type":"string","maxLength":120}
                }
              }
            },
            "detectedLanguages":{"type":"array","maxItems":4,"items":{"type":"string","maxLength":35}},
            "warnings":{"type":"array","maxItems":4,"items":{"type":"string","maxLength":120}}
          }
        }
        """;

    public static string NormalizeGeneratedOutput(string rawOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawOutput);
        try
        {
            using var document = JsonDocument.Parse(rawOutput, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new QwenStructuredOutputException("qwen.schema-validation-failed");
            }

            var actualProperties = root.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (actualProperties.SetEquals(RootProperties))
            {
                var normalized = NormalizeSingleExcessVisualFact(root, rawOutput);
                ValidateJsonShape(normalized);
                return normalized;
            }

            ValidateExactProperties(root, GeneratedRootProperties);
            ValidateString(root.GetProperty("schemaVersion"), 64);
            ValidateString(root.GetProperty("title"), 80);
            ValidateString(root.GetProperty("summary"), 320);
            ValidateStringArray(root.GetProperty("visualFacts"), 3, 120);
            ValidateStringArray(root.GetProperty("detectedLanguages"), 4, 35);
            ValidateStringArray(root.GetProperty("warnings"), 4, 120);

            var generated = JsonSerializer.Deserialize<GeneratedOutput>(rawOutput, JsonOptions)
                ?? throw new QwenStructuredOutputException("qwen.invalid-json");
            return JsonSerializer.Serialize(new
            {
                schemaVersion = generated.SchemaVersion,
                title = generated.Title,
                summary = generated.Summary,
                visualFacts = generated.VisualFacts ?? [],
                categoryIds = Array.Empty<string>(),
                entities = Array.Empty<object>(),
                detectedLanguages = generated.DetectedLanguages ?? [],
                warnings = generated.Warnings ?? [],
            }, JsonOptions);
        }
        catch (QwenStructuredOutputException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new QwenStructuredOutputException("qwen.invalid-json", exception);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
    };
    private static readonly HashSet<string> RootProperties = new(
        ["schemaVersion", "visualFacts", "title", "summary", "categoryIds", "entities", "detectedLanguages", "warnings"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> GeneratedRootProperties = new(
        ["schemaVersion", "title", "summary", "visualFacts", "detectedLanguages", "warnings"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> EntityProperties = new(
        ["kind", "rawText", "normalizedValue", "evidence"],
        StringComparer.Ordinal);

    private static string NormalizeSingleExcessVisualFact(
        JsonElement root,
        string rawOutput)
    {
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != RootProperties.Count
            || !root.TryGetProperty("visualFacts", out var visualFacts)
            || visualFacts.ValueKind != JsonValueKind.Array
            || visualFacts.GetArrayLength() != 4
            || !root.TryGetProperty("warnings", out var warnings))
        {
            return rawOutput;
        }

        try
        {
            ValidateStringArray(visualFacts, 4, 120);
            // Preserve room for a visible, auditable normalization warning.
            ValidateStringArray(warnings, 3, 120);
        }
        catch (QwenStructuredOutputException)
        {
            return rawOutput;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in properties)
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals("visualFacts"))
                {
                    writer.WriteStartArray();
                    foreach (var item in visualFacts.EnumerateArray().Take(3))
                    {
                        item.WriteTo(writer);
                    }

                    writer.WriteEndArray();
                }
                else if (property.NameEquals("warnings"))
                {
                    writer.WriteStartArray();
                    foreach (var item in warnings.EnumerateArray())
                    {
                        item.WriteTo(writer);
                    }

                    writer.WriteStringValue(VisualFactsTruncatedWarning);
                    writer.WriteEndArray();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static bool TryExtractCompleteJsonObject(string value, out string json)
    {
        ArgumentNullException.ThrowIfNull(value);
        json = string.Empty;
        var start = 0;
        while (start < value.Length && char.IsWhiteSpace(value[start]))
        {
            start++;
        }

        if (start == value.Length || value[start] != '{')
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }

                if (depth == 0)
                {
                    for (var trailing = index + 1; trailing < value.Length; trailing++)
                    {
                        if (!char.IsWhiteSpace(value[trailing]))
                        {
                            return false;
                        }
                    }

                    json = value.Substring(start, index - start + 1);
                    return true;
                }
            }
        }

        return false;
    }

    public VisionStructuredResult Parse(
        string rawOutput,
        OcrDocument ocrDocument,
        AnalysisCompositionContext compositionContext,
        AnalysisProvenance provenance,
        DateTimeOffset? referenceTimeUtc = null,
        string? timeZoneId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawOutput);
        ArgumentNullException.ThrowIfNull(ocrDocument);
        ArgumentNullException.ThrowIfNull(compositionContext);
        ArgumentNullException.ThrowIfNull(provenance);
        if (rawOutput.Length > MaximumOutputCharacters)
        {
            throw new QwenStructuredOutputException("qwen.output-too-large");
        }

        ValidateJsonShape(rawOutput);

        StructuredOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<StructuredOutput>(rawOutput, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new QwenStructuredOutputException("qwen.invalid-json", exception);
        }

        if (output is null || output.SchemaVersion != SchemaVersion)
        {
            throw new QwenStructuredOutputException("qwen.schema-version-mismatch");
        }

        var visualFacts = NormalizeList(output.VisualFacts, 3, 120);
        var semanticWarnings = new List<string>();
        var allowedCategoryIds = compositionContext.Categories.Select(category => category.Id).ToHashSet();
        var categoryIds = new List<Guid>();
        foreach (var value in output.CategoryIds ?? [])
        {
            if (!Guid.TryParse(value, out var categoryId) || !allowedCategoryIds.Contains(categoryId))
            {
                AddWarning(semanticWarnings, "qwen.invalid-category-id-ignored");
                continue;
            }

            if (!categoryIds.Contains(categoryId))
            {
                categoryIds.Add(categoryId);
            }
        }

        // Reminder discovery is intentionally dual-source. OCR-correlated model
        // candidates retain the stronger interpretation label; candidates recovered
        // only from the image remain editable, lower-trust suggestions. Neither path
        // confirms a reminder or upgrades model output into an OCR fact.
        var evidenceCorpus = ocrDocument.Text ?? string.Empty;
        var isRemoteDirectImage =
            provenance.ExecutionLocation == AnalysisExecutionLocation.RemoteApi
            && provenance.RemoteInputMode == RemoteInputMode.DirectImage;
        var entities = new List<EntityCandidateDraft>();
        foreach (var entity in output.Entities ?? [])
        {
            if (entity is null
                || !AllowedEntityKinds.Contains(entity.Kind ?? string.Empty)
                || string.IsNullOrWhiteSpace(entity.RawText)
                || string.IsNullOrWhiteSpace(entity.Evidence))
            {
                AddWarning(semanticWarnings, "qwen.invalid-entity-evidence-ignored");
                continue;
            }

            var isOcrCorroborated =
                evidenceCorpus.Contains(entity.RawText, StringComparison.Ordinal)
                && evidenceCorpus.Contains(entity.Evidence, StringComparison.Ordinal);
            if (!isOcrCorroborated && !isRemoteDirectImage)
            {
                AddWarning(semanticWarnings, "qwen.entity-not-corroborated-by-ocr");
            }

            var kind = NormalizeEntityKind(entity.Kind!);
            var normalizedValue = string.IsNullOrWhiteSpace(entity.NormalizedValue)
                ? null
                : Limit(entity.NormalizedValue, 120);
            if (kind == "DateTime"
                && normalizedValue is not null
                && !IsSupportedNormalizedDate(normalizedValue))
            {
                normalizedValue = null;
                AddWarning(semanticWarnings, "qwen.invalid-normalized-date-ignored");
            }

            entities.Add(new EntityCandidateDraft(
                kind,
                Limit(entity.RawText, 80),
                normalizedValue,
                Limit(entity.Evidence, 120),
                "Model")
            {
                ReferenceTimeUtc = referenceTimeUtc,
                TimeZoneId = timeZoneId,
                AmbiguityReason = isRemoteDirectImage
                    ? "RemoteVisionNoLocalOcrEvidence"
                    : kind == "DateTime"
                        ? isOcrCorroborated
                            ? "ModelInterpretation"
                            : "ModelOnlyInterpretation"
                        : null,
            });
        }

        var languages = NormalizeList(output.DetectedLanguages, 4, 35)
            .Where(language =>
            {
                if (IsBcp47Tag(language))
                {
                    return true;
                }

                AddWarning(semanticWarnings, "qwen.invalid-language-tag-ignored");
                return false;
            })
            .ToArray();
        if (languages.Length == 0)
        {
            languages = ocrDocument.LanguageTags.ToArray();
        }

        var title = Limit(output.Title ?? string.Empty, 80);
        var rawSummary = Limit(output.Summary ?? string.Empty, 320);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new QwenStructuredOutputException("qwen.title-empty");
        }

        ValidateDraftQuality(
            title,
            rawSummary,
            evidenceCorpus,
            requireOcrNumericEvidence: !isRemoteDirectImage);
        var summary = NormalizeSummary(rawSummary, out var summaryShortened);
        if (summaryShortened)
        {
            AddWarning(semanticWarnings, "qwen.summary-shortened-to-complete-sentence");
        }

        var warnings = NormalizeList(
            (output.Warnings ?? []).Concat(semanticWarnings).ToArray(),
            8,
            120);

        var draft = new ExtractiveContentDraft(title, summary, languages, warnings, provenance)
        {
            SuggestedCategoryIds = categoryIds,
            EntityCandidates = entities,
        };
        return new VisionStructuredResult(visualFacts, draft, languages, warnings, provenance);
    }

    private static readonly HashSet<string> AllowedEntityKinds = new(
        ["date", "time", "datetime", "location", "address"],
        StringComparer.Ordinal);

    private static string NormalizeEntityKind(string kind) => kind switch
    {
        "date" or "time" or "datetime" => "DateTime",
        "location" or "address" => "Location",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool IsSupportedNormalizedDate(string value) =>
        DateTime.TryParseExact(
            value,
            ["yyyy", "yyyy-MM", "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (!warnings.Contains(warning, StringComparer.Ordinal))
        {
            warnings.Add(warning);
        }
    }

    private static void ValidateDraftQuality(
        string title,
        string summary,
        string evidenceCorpus,
        bool requireOcrNumericEvidence = true)
    {
        var combined = string.Join(
            ' ',
            new[] { title, summary }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var tokens = ExtractAlphaNumericTokens(combined);
        if (tokens.Count >= 12)
        {
            var mostFrequent = tokens
                .GroupBy(token => token, StringComparer.Ordinal)
                .Max(group => group.Count());
            var distinctCount = tokens.Distinct(StringComparer.Ordinal).Count();
            if ((mostFrequent >= 8 && mostFrequent * 100 >= tokens.Count * 40)
                || (tokens.Count >= 20 && distinctCount * 100 < tokens.Count * 20))
            {
                throw new QwenStructuredOutputException("qwen.degenerate-text-output");
            }
        }

        var letterCount = 0;
        var numberCount = 0;
        foreach (var rune in combined.EnumerateRunes())
        {
            if (IsLetterOrMark(rune))
            {
                letterCount++;
            }
            else if (IsNumber(rune))
            {
                numberCount++;
            }
        }

        if (numberCount >= 12
            && letterCount < 3
            && numberCount * 100 >= (letterCount + numberCount) * 90)
        {
            throw new QwenStructuredOutputException("qwen.degenerate-text-output");
        }

        if (!requireOcrNumericEvidence)
        {
            return;
        }

        foreach (var numericFact in ExtractNumericFacts(combined))
        {
            if (numericFact.Length >= 2
                && !evidenceCorpus.Contains(numericFact, StringComparison.Ordinal))
            {
                throw new QwenStructuredOutputException("qwen.ungrounded-numeric-output");
            }
        }
    }

    private static IReadOnlyList<string> ExtractAlphaNumericTokens(string value)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsLetterOrMark(rune) || IsNumber(rune))
            {
                token.Append(rune);
                continue;
            }

            AddToken(tokens, token);
        }

        AddToken(tokens, token);
        return tokens;
    }

    private static IEnumerable<string> ExtractNumericFacts(string value)
    {
        var fact = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsNumber(rune))
            {
                fact.Append(rune);
                continue;
            }

            if (fact.Length > 0)
            {
                yield return fact.ToString();
                fact.Clear();
            }
        }

        if (fact.Length > 0)
        {
            yield return fact.ToString();
        }
    }

    private static void AddToken(List<string> tokens, StringBuilder token)
    {
        if (token.Length == 0)
        {
            return;
        }

        tokens.Add(token.ToString().ToUpperInvariant());
        token.Clear();
    }

    private static bool IsLetterOrMark(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or
        UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or
        UnicodeCategory.OtherLetter or
        UnicodeCategory.NonSpacingMark or
        UnicodeCategory.SpacingCombiningMark or
        UnicodeCategory.EnclosingMark;

    private static bool IsNumber(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.DecimalDigitNumber or
        UnicodeCategory.LetterNumber or
        UnicodeCategory.OtherNumber;

    private static void ValidateJsonShape(string rawOutput)
    {
        try
        {
            using var document = JsonDocument.Parse(rawOutput, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new QwenStructuredOutputException("qwen.schema-validation-failed");
            }

            ValidateExactProperties(root, RootProperties);
            ValidateString(root.GetProperty("schemaVersion"), 64);
            ValidateStringArray(root.GetProperty("visualFacts"), 3, 120);
            ValidateString(root.GetProperty("title"), 80);
            ValidateString(root.GetProperty("summary"), 320);
            ValidateStringArray(root.GetProperty("categoryIds"), 4, 64);
            ValidateStringArray(root.GetProperty("detectedLanguages"), 4, 35);
            ValidateStringArray(root.GetProperty("warnings"), 4, 120);

            var entities = root.GetProperty("entities");
            if (entities.ValueKind != JsonValueKind.Array || entities.GetArrayLength() > 3)
            {
                throw new QwenStructuredOutputException("qwen.schema-validation-failed");
            }

            foreach (var entity in entities.EnumerateArray())
            {
                if (entity.ValueKind != JsonValueKind.Object)
                {
                    throw new QwenStructuredOutputException("qwen.schema-validation-failed");
                }

                ValidateExactProperties(entity, EntityProperties);
                ValidateString(entity.GetProperty("kind"), 32);
                ValidateString(entity.GetProperty("rawText"), 80);
                var normalized = entity.GetProperty("normalizedValue");
                if (normalized.ValueKind != JsonValueKind.Null)
                {
                    ValidateString(normalized, 120);
                }

                ValidateString(entity.GetProperty("evidence"), 120);
            }
        }
        catch (QwenStructuredOutputException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new QwenStructuredOutputException("qwen.invalid-json", exception);
        }
    }

    private static void ValidateExactProperties(JsonElement element, HashSet<string> expected)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !actual.Add(property.Name))
            {
                throw new QwenStructuredOutputException("qwen.schema-validation-failed");
            }
        }

        if (!actual.SetEquals(expected))
        {
            throw new QwenStructuredOutputException("qwen.schema-validation-failed");
        }
    }

    private static void ValidateStringArray(JsonElement element, int maximumCount, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximumCount)
        {
            throw new QwenStructuredOutputException("qwen.schema-validation-failed");
        }

        foreach (var item in element.EnumerateArray())
        {
            ValidateString(item, maximumLength);
        }
    }

    private static void ValidateString(JsonElement element, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.String || (element.GetString()?.Length ?? 0) > maximumLength)
        {
            throw new QwenStructuredOutputException("qwen.schema-validation-failed");
        }
    }

    private static IReadOnlyList<string> NormalizeList(
        IReadOnlyList<string>? values,
        int maximumCount,
        int maximumTextElements) => (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => Limit(value, maximumTextElements))
        .Distinct(StringComparer.Ordinal)
        .Take(maximumCount)
        .ToArray();

    private static string Limit(string value, int maximumTextElements)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value.Trim());
        var builder = new StringBuilder(Math.Min(value.Length, maximumTextElements));
        var count = 0;
        while (count < maximumTextElements && enumerator.MoveNext())
        {
            builder.Append(enumerator.GetTextElement());
            count++;
        }

        return builder.ToString();
    }

    private static string NormalizeSummary(string value, out bool shortened)
    {
        const int maximumTextElements = 160;
        var trimmed = value.Trim();
        var enumerator = StringInfo.GetTextElementEnumerator(trimmed);
        var builder = new StringBuilder(Math.Min(trimmed.Length, maximumTextElements));
        var count = 0;
        var firstSentenceEnd = -1;
        var firstClauseEnd = -1;
        var firstClauseIsCjk = false;
        while (count < maximumTextElements && enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            builder.Append(element);
            count++;
            if (firstSentenceEnd < 0
                && count >= 12
                && element is "." or "!" or "?" or "。" or "！" or "？")
            {
                firstSentenceEnd = builder.Length;
            }
            else if (firstClauseEnd < 0
                     && count >= 40
                     && element is "," or "，" or ";" or "；")
            {
                firstClauseEnd = builder.Length - element.Length;
                firstClauseIsCjk = element is "，" or "；";
            }
        }

        var exceededPreferredLength = enumerator.MoveNext();
        var looksIncomplete = trimmed.Length > 0
            && trimmed[^1] is ',' or '，' or ';' or '；' or ':' or '：' or '-' or '—' or '、';
        if (!exceededPreferredLength && !looksIncomplete)
        {
            shortened = false;
            return trimmed;
        }

        shortened = true;
        if (firstSentenceEnd > 0)
        {
            return builder.ToString(0, firstSentenceEnd);
        }

        if (firstClauseEnd > 0)
        {
            return builder.ToString(0, firstClauseEnd).TrimEnd()
                + (firstClauseIsCjk ? "。" : ".");
        }

        var limited = builder.ToString().TrimEnd(
            ' ', ',', '，', ';', '；', ':', '：', '-', '—', '、');
        if (limited.Length == 0
            || limited[^1] is '.' or '!' or '?' or '。' or '！' or '？' or '…')
        {
            return limited;
        }

        return limited + "…";
    }

    private static bool IsBcp47Tag(string value)
    {
        var parts = value.Split('-', StringSplitOptions.None);
        return parts.Length > 0
            && parts[0].Length is 2 or 3
            && parts[0].All(char.IsAsciiLetter)
            && parts.Skip(1).All(part => part.Length is >= 1 and <= 8
                && part.All(char.IsAsciiLetterOrDigit));
    }

    private sealed class StructuredOutput
    {
        public string? SchemaVersion { get; init; }
        public string[]? VisualFacts { get; init; }
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string[]? CategoryIds { get; init; }
        public StructuredEntity?[]? Entities { get; init; }
        public string[]? DetectedLanguages { get; init; }
        public string[]? Warnings { get; init; }
    }

    private sealed class GeneratedOutput
    {
        public string? SchemaVersion { get; init; }
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string[]? VisualFacts { get; init; }
        public string[]? DetectedLanguages { get; init; }
        public string[]? Warnings { get; init; }
    }

    private sealed class StructuredEntity
    {
        public string? Kind { get; init; }
        public string? RawText { get; init; }
        public string? NormalizedValue { get; init; }
        public string? Evidence { get; init; }
    }
}

public sealed class QwenStructuredOutputException : Exception, IModelOperationFailure
{
    public QwenStructuredOutputException(string errorCode, Exception? innerException = null)
        : base("The local vision model returned an invalid structured result.", innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
