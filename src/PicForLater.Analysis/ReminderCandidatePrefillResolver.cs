using PicForLater.Core.Analysis;
using PicForLater.Core.Reminders;

namespace PicForLater.Analysis;

/// <summary>
/// Deterministically reparses auditable evidence when a legacy candidate lost
/// its normalized value or a model interpretation needs fact-layer validation.
/// This does not update the stored fact or confirm a reminder.
/// </summary>
public sealed class ReminderCandidatePrefillResolver
{
    private static readonly string[] SupportedNormalizedFormats =
    [
        "yyyy",
        "yyyy-MM",
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
    ];
    private readonly DeterministicEntityExtractor _extractor = new();

    public ReminderDatePrefill? Resolve(ReminderCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Kind != EntityCandidateKind.DateTime)
        {
            return null;
        }

        return Resolve(
            candidate.NormalizedValue,
            candidate.RawText,
            candidate.Evidence,
            candidate.ReferenceTimeUtc ?? candidate.GeneratedAtUtc,
            candidate.TimeZoneId ?? TimeZoneInfo.Local.Id,
            ["und"]);
    }

    public ReminderDatePrefill? Resolve(
        EntityCandidateDraft candidate,
        DateTimeOffset fallbackReferenceTimeUtc,
        string fallbackTimeZoneId,
        IReadOnlyList<string>? languageTags = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.Kind.Equals("DateTime", StringComparison.Ordinal))
        {
            return null;
        }

        return Resolve(
            candidate.NormalizedValue,
            candidate.RawText,
            candidate.Evidence,
            candidate.ReferenceTimeUtc ?? fallbackReferenceTimeUtc,
            candidate.TimeZoneId ?? fallbackTimeZoneId,
            languageTags,
            ignoreExistingNormalizedValue: false);
    }

    public ReminderDatePrefill? ResolveEvidence(
        EntityCandidateDraft candidate,
        DateTimeOffset fallbackReferenceTimeUtc,
        string fallbackTimeZoneId,
        IReadOnlyList<string>? languageTags = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.Kind.Equals("DateTime", StringComparison.Ordinal))
        {
            return null;
        }

        return Resolve(
            candidate.NormalizedValue,
            candidate.RawText,
            candidate.Evidence,
            candidate.ReferenceTimeUtc ?? fallbackReferenceTimeUtc,
            candidate.TimeZoneId ?? fallbackTimeZoneId,
            languageTags,
            ignoreExistingNormalizedValue: true);
    }

    private ReminderDatePrefill? Resolve(
        string? normalizedValue,
        string rawText,
        string evidenceText,
        DateTimeOffset referenceTimeUtc,
        string timeZoneId,
        IReadOnlyList<string>? languageTags,
        bool ignoreExistingNormalizedValue = false)
    {
        if (!ignoreExistingNormalizedValue
            && IsSupportedNormalizedValue(normalizedValue))
        {
            return null;
        }

        var evidence = string.IsNullOrWhiteSpace(evidenceText)
            ? rawText
            : evidenceText;
        var line = new OcrLine(
            evidence,
            new OcrBoundingBox(0, 0, 1, 1),
            [],
            1);
        var document = new OcrDocument(
            evidence,
            [line],
            languageTags is { Count: > 0 } ? languageTags : ["und"],
            [],
            new AnalysisProvenance(
                "local.reminder-prefill",
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "reminder-prefill.v1",
                AnalysisExecutionLocation.Local,
                AnalysisOutputKind.OcrFacts),
            1,
            1);
        var resolved = _extractor.Extract(
                document,
                referenceTimeUtc,
                timeZoneId)
            .Candidates
            .Where(value =>
                value.Kind == "DateTime"
                && !string.IsNullOrWhiteSpace(value.NormalizedValue))
            .Select(value => new ReminderDatePrefill(
                value.NormalizedValue!,
                value.AmbiguityReason))
            .Distinct()
            .ToArray();

        return resolved.Length == 1 ? resolved[0] : null;
    }

    private static bool IsSupportedNormalizedValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateTime.TryParseExact(
            value,
            SupportedNormalizedFormats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _);
}

public sealed record ReminderDatePrefill(
    string NormalizedValue,
    string? AmbiguityReason);
