using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using PicForLater.Core.Analysis;
using RecognizerCulture = Microsoft.Recognizers.Text.Culture;

namespace PicForLater.Analysis;

/// <summary>
/// Produces auditable local candidates from the OCR fact layer. Natural-language
/// date/time interpretation is delegated to Microsoft Recognizers Text instead of
/// growing application-specific expression rules. Location matching remains a
/// deliberately conservative OCR suffix/cue fallback; a configured local model can
/// contribute broader semantic location candidates later in the analysis pipeline.
/// </summary>
public sealed partial class DeterministicEntityExtractor : IEntityExtractor
{
    private const string DateTimeCandidateKind = "DateTime";
    private const string LocationCandidateKind = "Location";
    private static readonly string[] SupportedDateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
        "HH:mm:ss",
        "HH:mm",
    ];
    private static readonly string[] RelativeDateMarkers =
    [
        "今天", "今晚", "今夜", "明天", "明晚", "后天",
        "today", "tonight", "tomorrow",
        "hoy", "esta noche", "mañana",
        "aujourd'hui", "ce soir", "demain",
        "hoje", "esta noite", "amanhã",
        "heute", "heute abend", "morgen",
        "oggi", "stasera", "domani",
        "bugün", "bu gece", "yarın",
    ];

    public EntityExtractionResult Extract(
        OcrDocument ocrDocument,
        DateTimeOffset referenceTimeUtc,
        string timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(ocrDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        var candidates = new List<EntityCandidateDraft>();
        var warnings = new List<string>();
        ExtractNaturalLanguageDateTimes(
            ocrDocument,
            referenceTimeUtc,
            timeZoneId,
            candidates,
            warnings);
        foreach (var line in ocrDocument.Lines)
        {
            ExtractLocations(line, referenceTimeUtc, timeZoneId, candidates);
        }

        var unique = candidates
            .GroupBy(
                candidate => string.Join(
                    "\u001f",
                    candidate.Kind,
                    candidate.RawText,
                    candidate.NormalizedValue ?? string.Empty,
                    candidate.Evidence),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return new EntityExtractionResult(
            unique,
            ocrDocument.LanguageTags,
            warnings,
            new AnalysisProvenance(
                "local.deterministic-entities",
                "Microsoft.Recognizers.Text.DateTime",
                "1.8.13",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "recognizers-text-entities.v1",
                AnalysisExecutionLocation.Local,
                AnalysisOutputKind.DeterministicEntityCandidates));
    }

    private static void ExtractNaturalLanguageDateTimes(
        OcrDocument document,
        DateTimeOffset referenceTimeUtc,
        string timeZoneId,
        ICollection<EntityCandidateDraft> candidates,
        ICollection<string> warnings)
    {
        var documentCultures = GetRecognizerCultures(document.LanguageTags);
        if (documentCultures.Count == 0)
        {
            warnings.Add("datetime-recognizer-language-unsupported");
            return;
        }

        var referenceLocal = ConvertReferenceTime(referenceTimeUtc, timeZoneId);
        foreach (var line in document.Lines)
        {
            var cultures = SelectCulturesForLine(documentCultures, line.Text);
            var recognized = new List<RecognizedDateTime>();
            foreach (var recognitionInput in CreateRecognitionInputs(line.Text))
            {
                foreach (var culture in cultures)
                {
                    IReadOnlyList<ModelResult> results;
                    try
                    {
                        results = DateTimeRecognizer.RecognizeDateTime(
                            recognitionInput.Text,
                            culture,
                            DateTimeOptions.None,
                            referenceLocal);
                    }
                    catch (ArgumentException)
                    {
                        warnings.Add("datetime-recognizer-culture-failed");
                        continue;
                    }

                    foreach (var result in results)
                    {
                        if (TryResolveDateTime(
                                result,
                                recognitionInput,
                                line.Text,
                                referenceLocal,
                                out var resolved))
                        {
                            recognized.Add(resolved);
                        }
                    }
                }
            }

            var resolvedGroups = new List<RecognizedDateTime>();
            foreach (var group in recognized.GroupBy(
                         value => (value.Start, value.Length),
                         EqualityComparer<(int Start, int Length)>.Default))
            {
                var values = group.ToArray();
                var normalizedValues = values
                    .Select(value => value.NormalizedValue)
                    .Where(value => value is not null)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var ambiguity = values
                    .Select(value => value.AmbiguityReason)
                    .FirstOrDefault(value => value is not null);
                string? normalized = normalizedValues.Length switch
                {
                    0 => null,
                    1 => normalizedValues[0],
                    _ => null,
                };
                if (normalizedValues.Length > 1)
                {
                    ambiguity = "DateOrder";
                }

                var first = values[0];
                resolvedGroups.Add(first with
                {
                    NormalizedValue = normalized,
                    AmbiguityReason = ambiguity,
                    HasDate = values.Any(value => value.HasDate),
                    HasTime = values.Any(value => value.HasTime),
                });
            }

            foreach (var resolved in CombineAdjacentDateAndTimeFragments(
                         line.Text,
                         resolvedGroups))
            {
                candidates.Add(CreateCandidate(
                    line,
                    DateTimeCandidateKind,
                    resolved.RawText,
                    resolved.HasDate ? resolved.NormalizedValue : null,
                    referenceTimeUtc,
                    timeZoneId,
                    resolved.AmbiguityReason));
            }
        }

        CombineDateAndTimeAcrossAdjacentOcrLines(
            document,
            referenceTimeUtc,
            timeZoneId,
            candidates);
    }

    private static void CombineDateAndTimeAcrossAdjacentOcrLines(
        OcrDocument document,
        DateTimeOffset referenceTimeUtc,
        string timeZoneId,
        ICollection<EntityCandidateDraft> candidates)
    {
        if (document.Lines.Count < 2)
        {
            return;
        }

        for (var index = 0; index < document.Lines.Count - 1; index++)
        {
            var dateLine = document.Lines[index];
            var timeLine = document.Lines[index + 1];
            if (!AreRelatedAdjacentLines(dateLine.BoundingBox, timeLine.BoundingBox))
            {
                continue;
            }

            var dateFragments = candidates
                .Where(candidate =>
                    candidate.Kind == DateTimeCandidateKind
                    && candidate.BoundingBox == dateLine.BoundingBox
                    && DateTime.TryParseExact(
                        candidate.NormalizedValue,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _))
                .ToArray();
            var timeFragments = candidates
                .Where(candidate =>
                    candidate.Kind == DateTimeCandidateKind
                    && candidate.BoundingBox == timeLine.BoundingBox
                    && candidate.AmbiguityReason == "MissingDate"
                    && TryParseCandidateClock(candidate.RawText, out _))
                .ToArray();
            if (dateFragments.Length != 1 || timeFragments.Length != 1)
            {
                continue;
            }

            var combinedText = $"{dateLine.Text} {timeLine.Text}";
            var combinedBox = Union(dateLine.BoundingBox, timeLine.BoundingBox);
            var syntheticLine = new OcrLine(combinedText, combinedBox, [], null);
            var syntheticCandidates = new List<EntityCandidateDraft>();
            ExtractNaturalLanguageDateTimes(
                document with
                {
                    Text = combinedText,
                    Lines = [syntheticLine],
                },
                referenceTimeUtc,
                timeZoneId,
                syntheticCandidates,
                []);

            var dateFragment = dateFragments[0];
            var timeFragment = timeFragments[0];
            if (!TryParseCandidateClock(timeFragment.RawText, out var fragmentTime))
            {
                continue;
            }

            var fullCandidates = syntheticCandidates
                .Where(candidate =>
                    TryParseFullDateTime(candidate.NormalizedValue, out var fullDateTime)
                    && candidate.NormalizedValue!.StartsWith(
                        dateFragment.NormalizedValue!,
                        StringComparison.Ordinal)
                    && fullDateTime.TimeOfDay == fragmentTime)
                .ToArray();
            if (fullCandidates.Length != 1)
            {
                continue;
            }

            candidates.Remove(dateFragment);
            candidates.Remove(timeFragment);
            candidates.Add(fullCandidates[0] with
            {
                RawText = $"{dateFragment.RawText} {timeFragment.RawText}",
                Evidence = $"{dateLine.Text}{Environment.NewLine}{timeLine.Text}",
                BoundingBox = combinedBox,
                AmbiguityReason = dateFragment.AmbiguityReason,
            });
        }
    }

    private static bool AreRelatedAdjacentLines(
        OcrBoundingBox upper,
        OcrBoundingBox lower)
    {
        var maximumLineHeight = Math.Max(upper.Height, lower.Height);
        if (maximumLineHeight <= 0)
        {
            return false;
        }

        var verticalGap = lower.Y - (upper.Y + upper.Height);
        if (verticalGap < -maximumLineHeight
            || verticalGap > maximumLineHeight * 2.5)
        {
            return false;
        }

        var maximumLeftEdgeDelta = Math.Max(maximumLineHeight * 1.5, 0.04);
        if (Math.Abs(upper.X - lower.X) > maximumLeftEdgeDelta)
        {
            return false;
        }

        var overlap = Math.Min(upper.X + upper.Width, lower.X + lower.Width)
            - Math.Max(upper.X, lower.X);
        var minimumWidth = Math.Min(upper.Width, lower.Width);
        return overlap > 0
            && (minimumWidth <= 0 || overlap / minimumWidth >= 0.1);
    }

    private static OcrBoundingBox Union(OcrBoundingBox first, OcrBoundingBox second)
    {
        var x = Math.Min(first.X, second.X);
        var y = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);
        return new OcrBoundingBox(x, y, right - x, bottom - y);
    }

    private static bool TryParseCandidateClock(string rawText, out TimeSpan time)
    {
        var trimmed = rawText.Trim();
        if (TimeSpan.TryParseExact(
                trimmed,
                [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"],
                CultureInfo.InvariantCulture,
                out time))
        {
            return true;
        }

        if (DateTime.TryParse(
                trimmed,
                CultureInfo.CurrentCulture,
                DateTimeStyles.NoCurrentDateDefault,
                out var dateTime))
        {
            time = dateTime.TimeOfDay;
            return true;
        }

        time = default;
        return false;
    }

    private static bool TryParseFullDateTime(string? value, out DateTime dateTime) =>
        DateTime.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out dateTime);

    private static bool TryResolveDateTime(
        ModelResult result,
        RecognitionInput input,
        string originalText,
        DateTime referenceLocal,
        out RecognizedDateTime resolved)
    {
        resolved = default;
        if (result.Start < 0
            || result.End < result.Start
            || result.End >= input.Text.Length
            || IsEmbeddedDateTimeFragment(input.Text, result.Start, result.End))
        {
            return false;
        }

        var resolutionValues = GetResolutionValues(result.Resolution);
        if (resolutionValues.Count == 0)
        {
            return false;
        }

        var originalStart = input.OriginalIndices[result.Start];
        var originalEnd = input.OriginalIndices[result.End];
        var rawText = originalText.Substring(
            originalStart,
            originalEnd - originalStart + 1);
        var timexValues = resolutionValues
            .Select(value => value.GetValueOrDefault("timex"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var missingYear = timexValues.Any(value =>
            value!.Contains("XXXX", StringComparison.Ordinal));
        var valueTypes = resolutionValues
            .Select(value => value.GetValueOrDefault("type"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasTime = valueTypes.Any(value =>
            value!.Equals("datetime", StringComparison.OrdinalIgnoreCase));
        if (valueTypes.All(value => value!.Equals("time", StringComparison.OrdinalIgnoreCase)))
        {
            var normalizedTimes = resolutionValues
                .Select(value => value.GetValueOrDefault("value"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => TryParseResolution(value!, out var parsed)
                    ? parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : null)
                .Where(value => value is not null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            resolved = new RecognizedDateTime(
                originalStart,
                originalEnd - originalStart + 1,
                rawText,
                normalizedTimes.Length == 1 ? normalizedTimes[0] : null,
                "MissingDate",
                HasDate: false,
                HasTime: true);
            return true;
        }

        if (valueTypes.Any(value =>
                value!.Equals("daterange", StringComparison.OrdinalIgnoreCase))
            && TryGetPartialDateTimex(
                timexValues,
                out var partialDate,
                out var partialDateAmbiguity))
        {
            if (IsPartialDatePrefix(input.Text, result.End))
            {
                return false;
            }

            resolved = new RecognizedDateTime(
                originalStart,
                originalEnd - originalStart + 1,
                rawText,
                partialDate,
                partialDateAmbiguity,
                HasDate: true,
                HasTime: false);
            return true;
        }

        var parsedValues = resolutionValues
            .Select(value => value.GetValueOrDefault("value"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => TryParseResolution(value!, out var parsed)
                ? parsed
                : (DateTime?)null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        if (parsedValues.Length == 0)
        {
            return false;
        }

        DateTime selected;
        string? ambiguity = null;
        if (missingYear)
        {
            selected = parsedValues.FirstOrDefault(value =>
                value.Year == referenceLocal.Year);
            if (selected == default)
            {
                var template = parsedValues[0];
                try
                {
                    selected = new DateTime(
                        referenceLocal.Year,
                        template.Month,
                        template.Day,
                        template.Hour,
                        template.Minute,
                        template.Second,
                        DateTimeKind.Unspecified);
                }
                catch (ArgumentOutOfRangeException)
                {
                    resolved = new RecognizedDateTime(
                        originalStart,
                        originalEnd - originalStart + 1,
                        rawText,
                        NormalizedValue: null,
                        AmbiguityReason: "MissingYear",
                        HasDate: true,
                        HasTime: hasTime);
                    return true;
                }
            }

            ambiguity = "MissingYear";
        }
        else if (parsedValues.Length > 1)
        {
            var ambiguityReason = parsedValues
                .Select(value => value.Date)
                .Distinct()
                .Skip(1)
                .Any()
                ? "DateOrder"
                : "TimeOfDay";
            resolved = new RecognizedDateTime(
                originalStart,
                originalEnd - originalStart + 1,
                rawText,
                NormalizedValue: null,
                AmbiguityReason: ambiguityReason,
                HasDate: true,
                HasTime: hasTime);
            return true;
        }
        else
        {
            selected = parsedValues[0];
        }

        var normalized = selected.ToString(
            hasTime ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        resolved = new RecognizedDateTime(
            originalStart,
            originalEnd - originalStart + 1,
            rawText,
            normalized,
            ambiguity ?? (IsRelativeExpression(result.Text) ? "RelativeDate" : null),
            HasDate: true,
            HasTime: hasTime);
        return true;
    }

    private static IReadOnlyList<RecognizedDateTime> CombineAdjacentDateAndTimeFragments(
        string sourceText,
        IReadOnlyList<RecognizedDateTime> fragments)
    {
        if (fragments.Count < 2)
        {
            return fragments;
        }

        var combined = new List<RecognizedDateTime>();
        var consumed = new HashSet<int>();
        foreach (var timeEntry in fragments
                     .Select((value, index) => (Value: value, Index: index))
                     .Where(entry =>
                         !entry.Value.HasDate
                         && entry.Value.HasTime
                         && !string.IsNullOrWhiteSpace(entry.Value.NormalizedValue))
                     .OrderBy(entry => entry.Value.Start))
        {
            var dateEntry = fragments
                .Select((value, index) => (Value: value, Index: index))
                .Where(entry =>
                    !consumed.Contains(entry.Index)
                    && entry.Value.HasDate
                    && !entry.Value.HasTime
                    && entry.Value.End < timeEntry.Value.Start
                    && !string.IsNullOrWhiteSpace(entry.Value.NormalizedValue)
                    && IsDateTimeJoiner(
                        sourceText,
                        entry.Value.End + 1,
                        timeEntry.Value.Start - entry.Value.End - 1))
                .OrderByDescending(entry => entry.Value.RawText.Count(char.IsDigit))
                .ThenByDescending(entry => entry.Value.Start)
                .FirstOrDefault();
            if (dateEntry.Value == default
                || !TryCombineDateAndTime(
                    sourceText,
                    dateEntry.Value,
                    timeEntry.Value,
                    out var combinedValue))
            {
                continue;
            }

            combined.Add(combinedValue);
            for (var index = 0; index < fragments.Count; index++)
            {
                if (fragments[index].Start >= combinedValue.Start
                    && fragments[index].End <= combinedValue.End)
                {
                    consumed.Add(index);
                }
            }
        }

        return combined
            .Concat(fragments
                .Select((value, index) => (Value: value, Index: index))
                .Where(entry => !consumed.Contains(entry.Index))
                .Select(entry => entry.Value))
            .OrderBy(value => value.Start)
            .ThenByDescending(value => value.Length)
            .ToArray();
    }

    private static bool TryCombineDateAndTime(
        string sourceText,
        RecognizedDateTime date,
        RecognizedDateTime time,
        out RecognizedDateTime combined)
    {
        combined = default;
        if (!DateTime.TryParseExact(
                date.NormalizedValue,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate)
            || !TimeSpan.TryParseExact(
                time.NormalizedValue,
                @"hh\:mm\:ss",
                CultureInfo.InvariantCulture,
                out var parsedTime))
        {
            return false;
        }

        var start = date.Start;
        var end = time.End;
        combined = new RecognizedDateTime(
            start,
            end - start + 1,
            sourceText.Substring(start, end - start + 1),
            parsedDate.Date.Add(parsedTime).ToString(
                "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture),
            date.AmbiguityReason,
            HasDate: true,
            HasTime: true);
        return true;
    }

    private static bool IsDateTimeJoiner(string sourceText, int start, int length)
    {
        if (length < 0 || length > 12 || start < 0 || start + length > sourceText.Length)
        {
            return false;
        }

        foreach (var character in sourceText.AsSpan(start, length))
        {
            if (character is '\r'
                or '\n'
                or '。'
                or '！'
                or '？'
                or '；'
                or '，'
                or '!'
                or '?'
                or ';'
                or ',')
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<RecognitionInput> CreateRecognitionInputs(string text)
    {
        var identity = new RecognitionInput(
            text,
            Enumerable.Range(0, text.Length).ToArray());
        var normalizedText = new System.Text.StringBuilder(text.Length);
        var originalIndices = new List<int>(text.Length);
        for (var index = 0; index < text.Length;)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                normalizedText.Append(NormalizeRecognitionChar(text[index]));
                originalIndices.Add(index);
                index++;
                continue;
            }

            var whitespaceStart = index;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            var left = whitespaceStart > 0 ? text[whitespaceStart - 1] : (char?)null;
            var right = index < text.Length ? text[index] : (char?)null;
            if (ShouldRemoveOcrWhitespace(left, right))
            {
                continue;
            }

            normalizedText.Append(' ');
            originalIndices.Add(whitespaceStart);
        }

        var compact = normalizedText.ToString();
        return compact.Equals(text, StringComparison.Ordinal)
            ? [identity]
            : [identity, new RecognitionInput(compact, originalIndices.ToArray())];
    }

    private static char NormalizeRecognitionChar(char value) => value switch
    {
        >= '\uFF10' and <= '\uFF19' => (char)('0' + value - '\uFF10'),
        '\uFF1A' => ':',
        _ => value,
    };

    private static bool ShouldRemoveOcrWhitespace(char? left, char? right) =>
        left is char leftValue
        && right is char rightValue
        && (IsHan(leftValue) || IsHan(rightValue))
        && (IsHan(leftValue) || char.IsDigit(leftValue))
        && (IsHan(rightValue) || char.IsDigit(rightValue));

    private static bool IsHan(char value) =>
        value is >= '\u3400' and <= '\u4DBF'
        or >= '\u4E00' and <= '\u9FFF'
        or >= '\uF900' and <= '\uFAFF';

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> GetResolutionValues(
        IDictionary<string, object> resolution)
    {
        if (!resolution.TryGetValue("values", out var rawValues)
            || rawValues is not IEnumerable enumerable)
        {
            return [];
        }

        var values = new List<IReadOnlyDictionary<string, string>>();
        foreach (var value in enumerable)
        {
            if (value is IReadOnlyDictionary<string, string> readOnly)
            {
                values.Add(readOnly);
            }
            else if (value is IDictionary<string, string> dictionary)
            {
                values.Add(new Dictionary<string, string>(dictionary, StringComparer.Ordinal));
            }
        }

        return values;
    }

    private static bool TryParseResolution(string value, out DateTime parsed) =>
        DateTime.TryParseExact(
            value,
            SupportedDateTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out parsed);

    private static bool TryGetPartialDateTimex(
        IReadOnlyList<string?> timexValues,
        out string normalized,
        out string ambiguityReason)
    {
        normalized = string.Empty;
        ambiguityReason = string.Empty;
        var values = timexValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (values.Length != 1)
        {
            return false;
        }

        if (DateTime.TryParseExact(
                values[0],
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var yearMonth))
        {
            normalized = yearMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            ambiguityReason = "MissingDay";
            return true;
        }

        if (DateTime.TryParseExact(
                values[0],
                "yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var year))
        {
            normalized = year.ToString("yyyy", CultureInfo.InvariantCulture);
            ambiguityReason = "MissingMonthAndDay";
            return true;
        }

        return false;
    }

    private static bool IsRelativeExpression(string rawText) =>
        RelativeDateMarkers.Any(marker =>
            rawText.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsEmbeddedDateTimeFragment(string sourceText, int start, int end)
    {
        if (start > 0
            && char.IsDigit(sourceText[start])
            && char.IsDigit(sourceText[start - 1]))
        {
            return true;
        }

        return end + 1 < sourceText.Length
            && char.IsDigit(sourceText[end])
            && char.IsDigit(sourceText[end + 1]);
    }

    private static bool IsPartialDatePrefix(string sourceText, int end)
    {
        var next = end + 1;
        while (next < sourceText.Length && char.IsWhiteSpace(sourceText[next]))
        {
            next++;
        }

        return next < sourceText.Length
            && (char.IsDigit(sourceText[next])
                || sourceText[next] is '年' or '月' or '日' or '-' or '/' or '.');
    }

    private static IReadOnlyList<string> GetRecognizerCultures(
        IReadOnlyList<string> languageTags)
    {
        var cultures = new List<string>();
        var hasUndeterminedLanguage = false;
        foreach (var languageTag in languageTags)
        {
            var subtags = languageTag.Split(
                '-',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var primaryLanguage = subtags.FirstOrDefault()?.ToLowerInvariant();
            if (primaryLanguage == "und")
            {
                hasUndeterminedLanguage = true;
                if (subtags.Any(tag =>
                        tag.Equals("Hani", StringComparison.OrdinalIgnoreCase)
                        || tag.Equals("Hans", StringComparison.OrdinalIgnoreCase)
                        || tag.Equals("Hant", StringComparison.OrdinalIgnoreCase)))
                {
                    AddCulture(cultures, RecognizerCulture.Chinese);
                }

                if (subtags.Any(tag =>
                        tag.Equals("Latn", StringComparison.OrdinalIgnoreCase)))
                {
                    AddCulture(cultures, RecognizerCulture.English);
                }

                continue;
            }

            var culture = primaryLanguage switch
            {
                "zh" => RecognizerCulture.Chinese,
                "en" => RecognizerCulture.English,
                "es" => RecognizerCulture.Spanish,
                "fr" => RecognizerCulture.French,
                "pt" => RecognizerCulture.Portuguese,
                "de" => RecognizerCulture.German,
                "it" => RecognizerCulture.Italian,
                "tr" => RecognizerCulture.Turkish,
                _ => null,
            };
            if (culture is not null)
            {
                AddCulture(cultures, culture);
            }
        }

        if (cultures.Count == 0 && hasUndeterminedLanguage)
        {
            AddCulture(cultures, RecognizerCulture.Chinese);
            AddCulture(cultures, RecognizerCulture.English);
        }

        return cultures;
    }

    private static void AddCulture(ICollection<string> cultures, string culture)
    {
        if (!cultures.Contains(culture, StringComparer.Ordinal))
        {
            cultures.Add(culture);
        }
    }

    private static IReadOnlyList<string> SelectCulturesForLine(
        IReadOnlyList<string> documentCultures,
        string text)
    {
        IReadOnlyList<string> selectedCultures;
        var containsHan = text.Any(IsHan);
        if (containsHan
            && documentCultures.Contains(RecognizerCulture.Chinese, StringComparer.Ordinal))
        {
            selectedCultures = [RecognizerCulture.Chinese];
        }
        else if (!containsHan
                 && documentCultures.Count > 1
                 && documentCultures.Contains(RecognizerCulture.Chinese, StringComparer.Ordinal))
        {
            selectedCultures = documentCultures
                .Where(culture => !culture.Equals(
                    RecognizerCulture.Chinese,
                    StringComparison.Ordinal))
                .ToArray();
        }
        else
        {
            selectedCultures = documentCultures;
        }

        if (!CouldContainAmbiguousSlashDate(text))
        {
            return selectedCultures;
        }

        return selectedCultures
            .Concat([RecognizerCulture.English, RecognizerCulture.Chinese])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool CouldContainAmbiguousSlashDate(string text) =>
        text.Contains('/', StringComparison.Ordinal)
        && text.Count(char.IsDigit) >= 4;

    private static void ExtractLocations(
        OcrLine line,
        DateTimeOffset referenceTimeUtc,
        string timeZoneId,
        ICollection<EntityCandidateDraft> candidates)
    {
        foreach (var recognitionInput in CreateRecognitionInputs(line.Text))
        {
            var contextualMatches = ContextualChineseLocationRegex().Matches(
                recognitionInput.Text);
            foreach (Match match in contextualMatches)
            {
                var group = match.Groups["location"];
                AddLocationCandidate(
                    line,
                    recognitionInput,
                    group,
                    referenceTimeUtc,
                    timeZoneId,
                    candidates);
            }

            if (contextualMatches.Count == 0)
            {
                foreach (Match match in ChineseLocationRegex().Matches(recognitionInput.Text))
                {
                    AddLocationCandidate(
                        line,
                        recognitionInput,
                        match,
                        referenceTimeUtc,
                        timeZoneId,
                        candidates);
                }
            }

            foreach (Match match in EnglishLocationRegex().Matches(recognitionInput.Text))
            {
                AddLocationCandidate(
                    line,
                    recognitionInput,
                    match,
                    referenceTimeUtc,
                    timeZoneId,
                    candidates);
            }
        }
    }

    private static void AddLocationCandidate(
        OcrLine line,
        RecognitionInput recognitionInput,
        Group match,
        DateTimeOffset referenceTimeUtc,
        string timeZoneId,
        ICollection<EntityCandidateDraft> candidates)
    {
        if (!match.Success || match.Length == 0)
        {
            return;
        }

        var originalStart = recognitionInput.OriginalIndices[match.Index];
        var originalEnd = recognitionInput.OriginalIndices[match.Index + match.Length - 1];
        var rawText = line.Text.Substring(
            originalStart,
            originalEnd - originalStart + 1).Trim();
        var normalizedValue = match.Value.Trim();
        candidates.Add(CreateCandidate(
            line,
            LocationCandidateKind,
            rawText,
            normalizedValue,
            referenceTimeUtc,
            timeZoneId,
            ambiguityReason: null));
    }

    private static EntityCandidateDraft CreateCandidate(
        OcrLine line,
        string kind,
        string rawText,
        string? normalizedValue,
        DateTimeOffset referenceTimeUtc,
        string timeZoneId,
        string? ambiguityReason) =>
        new(
            kind,
            rawText,
            normalizedValue,
            line.Text,
            "Ocr")
        {
            BoundingBox = line.BoundingBox,
            ReferenceTimeUtc = referenceTimeUtc,
            TimeZoneId = timeZoneId,
            AmbiguityReason = ambiguityReason,
        };

    private static DateTime ConvertReferenceTime(
        DateTimeOffset referenceTimeUtc,
        string timeZoneId)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTime(referenceTimeUtc, zone).DateTime;
        }
        catch (TimeZoneNotFoundException)
        {
            return referenceTimeUtc.UtcDateTime;
        }
        catch (InvalidTimeZoneException)
        {
            return referenceTimeUtc.UtcDateTime;
        }
    }

    [GeneratedRegex(
        @"(?:前往|来到|到|在|去|来)\s*(?<location>[\p{L}\p{N}（）()·\-]{0,24}(?:路|街|道|巷|大道|大厦|广场|中心|会议室|礼堂|体育馆|机场|车站|公园)(?:\s*\d{1,5}\s*号?)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ContextualChineseLocationRegex();

    [GeneratedRegex(
        @"[\p{L}\p{N}（）()·\-]{2,32}(?:路|街|道|巷|大道|大厦|广场|中心|会议室|礼堂|体育馆|机场|车站|公园)(?:\s*\d{1,5}\s*号?)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChineseLocationRegex();

    [GeneratedRegex(
        @"\b(?:Room\s+[A-Za-z0-9-]+|\d{1,6}\s+(?:[\p{L}0-9.'-]+\s+){0,5}(?:Street|St|Road|Rd|Avenue|Ave|Boulevard|Blvd|Lane|Ln|Drive|Dr|Court|Ct))\b\.?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishLocationRegex();

    private readonly record struct RecognizedDateTime(
        int Start,
        int Length,
        string RawText,
        string? NormalizedValue,
        string? AmbiguityReason,
        bool HasDate,
        bool HasTime)
    {
        public int End => Start + Length - 1;
    }

    private sealed record RecognitionInput(
        string Text,
        IReadOnlyList<int> OriginalIndices);
}
