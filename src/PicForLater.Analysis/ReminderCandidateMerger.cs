using System.Globalization;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

/// <summary>
/// Reconciles reminder interpretations from the OCR fact layer and the optional
/// local model. The stage output remains editable evidence, but one actionable
/// instant is represented by one candidate.
/// </summary>
public sealed class ReminderCandidateMerger
{
    private static readonly string[] FullDateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
    ];

    private readonly ReminderCandidatePrefillResolver _prefillResolver = new();

    public IReadOnlyList<EntityCandidateDraft> Merge(
        IEnumerable<EntityCandidateDraft> ocrCandidates,
        IEnumerable<EntityCandidateDraft> modelCandidates,
        DateTimeOffset fallbackReferenceTimeUtc,
        string fallbackTimeZoneId,
        IReadOnlyList<string>? languageTags = null)
    {
        ArgumentNullException.ThrowIfNull(ocrCandidates);
        ArgumentNullException.ThrowIfNull(modelCandidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackTimeZoneId);

        var recovered = ocrCandidates
            .Concat(modelCandidates)
            .Select(candidate => RecoverNormalization(
                candidate,
                fallbackReferenceTimeUtc,
                fallbackTimeZoneId,
                languageTags))
            .ToArray();

        var merged = recovered
            .GroupBy(CreateEquivalenceKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(GetSourcePriority)
                .ThenByDescending(candidate => candidate.Evidence.Length)
                .First())
            .ToList();
        var mergedSnapshot = merged.ToArray();
        merged.RemoveAll(candidate => IsContainedLowerPrecisionFragment(
            candidate,
            mergedSnapshot));

        var fullDateTimes = merged
            .Where(candidate => TryParseFullDateTime(
                candidate.NormalizedValue,
                out _))
            .ToArray();
        if (fullDateTimes.Length == 0)
        {
            return merged;
        }

        return merged
            .Where(candidate =>
                fullDateTimes.Contains(candidate)
                || !IsSubsumedFragment(candidate, fullDateTimes))
            .ToArray();
    }

    private static bool IsContainedLowerPrecisionFragment(
        EntityCandidateDraft candidate,
        IReadOnlyList<EntityCandidateDraft> candidates)
    {
        if (!candidate.Kind.Equals("DateTime", StringComparison.Ordinal)
            || candidate.AmbiguityReason is not (
                "MissingYear"
                or "MissingDay"
                or "MissingMonthAndDay"
                or "MissingDate"))
        {
            return false;
        }

        var raw = CompactEvidence(candidate.RawText);
        if (raw.Length < 3)
        {
            return false;
        }

        return candidates.Any(other =>
            !ReferenceEquals(other, candidate)
            && other.Kind.Equals("DateTime", StringComparison.Ordinal)
            && other.Source.Equals(candidate.Source, StringComparison.Ordinal)
            && CompactEvidence(other.Evidence)
                .Equals(CompactEvidence(candidate.Evidence), StringComparison.Ordinal)
            && other.AmbiguityReason is not (
                "MissingYear"
                or "MissingDay"
                or "MissingMonthAndDay"
                or "MissingDate")
            && CompactEvidence(other.RawText).Length > raw.Length
            && CompactEvidence(other.RawText).Contains(raw, StringComparison.Ordinal));
    }

    private EntityCandidateDraft RecoverNormalization(
        EntityCandidateDraft candidate,
        DateTimeOffset fallbackReferenceTimeUtc,
        string fallbackTimeZoneId,
        IReadOnlyList<string>? languageTags)
    {
        if (!candidate.Kind.Equals("DateTime", StringComparison.Ordinal))
        {
            return candidate;
        }

        candidate = candidate with
        {
            ReferenceTimeUtc = candidate.ReferenceTimeUtc ?? fallbackReferenceTimeUtc,
            TimeZoneId = candidate.TimeZoneId ?? fallbackTimeZoneId,
        };
        if (candidate.Source.Equals("Model", StringComparison.Ordinal))
        {
            var evidenceResolved = _prefillResolver.ResolveEvidence(
                candidate,
                fallbackReferenceTimeUtc,
                fallbackTimeZoneId,
                languageTags);
            if (evidenceResolved is not null
                && (evidenceResolved.AmbiguityReason == "MissingYear"
                    || !evidenceResolved.NormalizedValue.Equals(
                        candidate.NormalizedValue,
                        StringComparison.Ordinal)))
            {
                return candidate with
                {
                    NormalizedValue = evidenceResolved.NormalizedValue,
                    AmbiguityReason = candidate.AmbiguityReason
                            == "RemoteVisionNoLocalOcrEvidence"
                        ? candidate.AmbiguityReason
                        : evidenceResolved.AmbiguityReason
                            ?? candidate.AmbiguityReason,
                };
            }
        }

        var recovered = _prefillResolver.Resolve(
            candidate,
            fallbackReferenceTimeUtc,
            fallbackTimeZoneId,
            languageTags);
        return recovered is null
            ? candidate
            : candidate with { NormalizedValue = recovered.NormalizedValue };
    }

    private static string CreateEquivalenceKey(EntityCandidateDraft candidate)
    {
        if (candidate.Kind.Equals("DateTime", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(candidate.NormalizedValue))
        {
            var normalizedValue = TryParseFullDateTime(
                candidate.NormalizedValue,
                out var dateTime)
                ? dateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                    CultureInfo.InvariantCulture)
                : candidate.NormalizedValue;
            return string.Join(
                "\u001f",
                candidate.Kind,
                normalizedValue,
                candidate.TimeZoneId ?? string.Empty);
        }

        return string.Join(
            "\u001f",
            candidate.Kind,
            candidate.RawText,
            candidate.NormalizedValue ?? string.Empty,
            candidate.Evidence);
    }

    private static int GetSourcePriority(EntityCandidateDraft candidate) =>
        candidate.Source switch
        {
            "Metadata" => 0,
            "Ocr" => 1,
            "Model" => 2,
            _ => 3,
        };

    private static bool IsSubsumedFragment(
        EntityCandidateDraft candidate,
        IReadOnlyList<EntityCandidateDraft> fullDateTimes)
    {
        if (!candidate.Kind.Equals("DateTime", StringComparison.Ordinal))
        {
            return false;
        }

        if (TryParseDate(candidate.NormalizedValue, out var date))
        {
            return fullDateTimes.Any(full =>
                TryParseFullDateTime(full.NormalizedValue, out var dateTime)
                && dateTime.Date == date.Date
                && EvidenceIsRelated(candidate, full));
        }

        if (candidate.AmbiguityReason == "MissingDate"
            && TryParseClock(candidate.RawText, out var time))
        {
            return fullDateTimes.Any(full =>
                TryParseFullDateTime(full.NormalizedValue, out var dateTime)
                && dateTime.TimeOfDay == time
                && EvidenceIsRelated(candidate, full));
        }

        return false;
    }

    private static bool EvidenceIsRelated(
        EntityCandidateDraft left,
        EntityCandidateDraft right)
    {
        var leftRaw = CompactEvidence(left.RawText);
        var rightRaw = CompactEvidence(right.RawText);
        var leftEvidence = CompactEvidence(left.Evidence);
        var rightEvidence = CompactEvidence(right.Evidence);
        return leftEvidence.Length >= 5
               && rightEvidence.Contains(leftEvidence, StringComparison.Ordinal)
            || rightEvidence.Length >= 5
               && leftEvidence.Contains(rightEvidence, StringComparison.Ordinal)
            || (leftRaw.Length >= 3
                   && rightEvidence.Contains(leftRaw, StringComparison.Ordinal)
                || rightRaw.Length >= 3
                   && leftEvidence.Contains(rightRaw, StringComparison.Ordinal))
               && HasMeaningfulContextOverlap(leftEvidence, rightEvidence);
    }

    private static string CompactEvidence(string value) =>
        string.Concat(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));

    private static bool HasMeaningfulContextOverlap(string left, string right)
    {
        var leftContext = string.Concat(left.Where(character => !char.IsDigit(character)));
        var rightContext = string.Concat(right.Where(character => !char.IsDigit(character)));
        const int minimumSharedLength = 4;
        if (leftContext.Length < minimumSharedLength
            || rightContext.Length < minimumSharedLength)
        {
            return false;
        }

        var shorter = leftContext.Length <= rightContext.Length
            ? leftContext
            : rightContext;
        var longer = ReferenceEquals(shorter, leftContext)
            ? rightContext
            : leftContext;
        for (var index = 0; index <= shorter.Length - minimumSharedLength; index++)
        {
            if (longer.Contains(
                    shorter.AsSpan(index, minimumSharedLength),
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseFullDateTime(string? value, out DateTime parsed) =>
        DateTime.TryParseExact(
            value,
            FullDateTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out parsed);

    private static bool TryParseDate(string? value, out DateTime parsed) =>
        DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out parsed);

    private static bool TryParseClock(string value, out TimeSpan parsed)
    {
        var compact = value.Trim();
        return TimeSpan.TryParseExact(
                   compact,
                   [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"],
                   CultureInfo.InvariantCulture,
                   out parsed)
               || DateTime.TryParse(
                   compact,
                   CultureInfo.CurrentCulture,
                   DateTimeStyles.NoCurrentDateDefault,
                   out var clock)
               && (parsed = clock.TimeOfDay) >= TimeSpan.Zero;
    }
}
