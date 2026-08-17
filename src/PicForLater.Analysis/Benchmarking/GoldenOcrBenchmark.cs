using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Benchmarking;

public sealed class GoldenOcrSample
{
    public string Id { get; init; } = string.Empty;

    public string Image { get; init; } = string.Empty;

    public string LanguageTag { get; init; } = string.Empty;

    public string Script { get; init; } = string.Empty;

    public string ExpectedText { get; init; } = string.Empty;

    public string[] ExpectedFields { get; init; } = [];

    public bool ExpectedPpOcrSupport { get; init; }

    public string Source { get; init; } = string.Empty;

    public string License { get; init; } = string.Empty;
}

public sealed record GoldenOcrSampleResult(
    string Id,
    bool ProviderClaimsSupport,
    bool SupportExpectationMet,
    double? CharacterErrorRate,
    double? FieldRecall,
    bool? LineOrderCorrect,
    double? BoundingBoxCoverage,
    TimeSpan Elapsed,
    long PrivateMemoryDeltaBytes,
    IReadOnlyList<string> Warnings);

public sealed record GoldenOcrBenchmarkReport(
    string ProviderId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<GoldenOcrSampleResult> Samples)
{
    public double MeanCharacterErrorRate =>
        Samples.Where(sample => sample.CharacterErrorRate.HasValue)
            .Select(sample => sample.CharacterErrorRate!.Value)
            .DefaultIfEmpty(1)
            .Average();

    public double MeanFieldRecall =>
        Samples.Where(sample => sample.FieldRecall.HasValue)
            .Select(sample => sample.FieldRecall!.Value)
            .DefaultIfEmpty(0)
            .Average();
}

public static class GoldenOcrBenchmark
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<GoldenOcrBenchmarkReport> RunAsync(
        IOcrProvider provider,
        string samplesDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(samplesDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(samplesDirectory));
        var manifestPath = ResolveUnderRoot(root, "golden-samples.json");
        await using var manifestStream = File.OpenRead(manifestPath);
        var samples = await JsonSerializer.DeserializeAsync<GoldenOcrSample[]>(
            manifestStream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The golden OCR sample manifest is empty.");
        ValidateSamples(samples);
        _ = await provider.IsAvailableAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<GoldenOcrSampleResult>(samples.Length);
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimsSupport = SupportsLanguage(
                provider.Descriptor.SupportedLanguageTags,
                sample.LanguageTag);
            if (!sample.ExpectedPpOcrSupport)
            {
                results.Add(new GoldenOcrSampleResult(
                    sample.Id,
                    claimsSupport,
                    SupportExpectationMet: !claimsSupport,
                    CharacterErrorRate: null,
                    FieldRecall: null,
                    LineOrderCorrect: null,
                    BoundingBoxCoverage: null,
                    TimeSpan.Zero,
                    PrivateMemoryDeltaBytes: 0,
                    []));
                continue;
            }

            var imagePath = ResolveUnderRoot(root, sample.Image);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var memoryBefore = process.PrivateMemorySize64;
            var stopwatch = Stopwatch.StartNew();
            OcrDocument document;
            try
            {
                document = await provider.RecognizeAsync(
                    new OcrRequest(
                        cancellationTokenValue => new ValueTask<Stream>(
                            Task.FromResult<Stream>(new FileStream(
                                imagePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                64 * 1024,
                                FileOptions.Asynchronous | FileOptions.SequentialScan))),
                        Path.GetFileName(imagePath),
                        PixelWidth: 0,
                        PixelHeight: 0,
                        [sample.LanguageTag]),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OcrProviderUnavailableException exception)
            {
                stopwatch.Stop();
                results.Add(new GoldenOcrSampleResult(
                    sample.Id,
                    claimsSupport,
                    SupportExpectationMet: false,
                    CharacterErrorRate: null,
                    FieldRecall: null,
                    LineOrderCorrect: null,
                    BoundingBoxCoverage: null,
                    stopwatch.Elapsed,
                    PrivateMemoryDeltaBytes: 0,
                    [exception.ErrorCode]));
                continue;
            }

            stopwatch.Stop();
            process.Refresh();
            var actual = Normalize(document.Text);
            var expected = Normalize(sample.ExpectedText);
            results.Add(new GoldenOcrSampleResult(
                sample.Id,
                claimsSupport,
                SupportExpectationMet: claimsSupport,
                CharacterErrorRate: CalculateCharacterErrorRate(expected, actual),
                FieldRecall: CalculateFieldRecall(sample.ExpectedFields, actual),
                LineOrderCorrect: HasExpectedLineOrder(sample.ExpectedText, actual),
                BoundingBoxCoverage: document.Lines.Count == 0
                    ? 0
                    : document.Lines.Count(line => IsValid(line.BoundingBox, document.ImageWidth, document.ImageHeight))
                      / (double)document.Lines.Count,
                stopwatch.Elapsed,
                process.PrivateMemorySize64 - memoryBefore,
                document.Warnings));
        }

        return new GoldenOcrBenchmarkReport(
            provider.Descriptor.ProviderId,
            DateTimeOffset.UtcNow,
            results);
    }

    public static double CalculateCharacterErrorRate(string expected, string actual)
    {
        var left = Normalize(expected).EnumerateRunes().ToArray();
        var right = Normalize(actual).EnumerateRunes().ToArray();
        if (left.Length == 0)
        {
            return right.Length == 0 ? 0 : 1;
        }

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1]
                    + (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length] / (double)left.Length;
    }

    private static double CalculateFieldRecall(IEnumerable<string> fields, string actual)
    {
        var values = fields.Select(Normalize).Where(field => field.Length > 0).ToArray();
        return values.Length == 0
            ? 1
            : values.Count(field => actual.Contains(field, StringComparison.Ordinal)) / (double)values.Length;
    }

    private static bool HasExpectedLineOrder(string expected, string actual)
    {
        var position = 0;
        foreach (var line in expected.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(Normalize))
        {
            var found = actual.IndexOf(line, position, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            position = found + line.Length;
        }

        return true;
    }

    private static bool IsValid(OcrBoundingBox box, int width, int height) =>
        box.X >= 0
        && box.Y >= 0
        && box.Width > 0
        && box.Height > 0
        && box.X + box.Width <= width + 1
        && box.Y + box.Height <= height + 1;

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString().Trim();
    }

    private static void ValidateSamples(IReadOnlyList<GoldenOcrSample> samples)
    {
        if (samples.Count == 0
            || samples.GroupBy(sample => sample.Id, StringComparer.Ordinal).Any(group => group.Count() > 1)
            || samples.Any(sample =>
                string.IsNullOrWhiteSpace(sample.Id)
                || string.IsNullOrWhiteSpace(sample.Image)
                || string.IsNullOrWhiteSpace(sample.LanguageTag)
                || string.IsNullOrWhiteSpace(sample.Script)
                || string.IsNullOrWhiteSpace(sample.Source)
                || string.IsNullOrWhiteSpace(sample.License)))
        {
            throw new InvalidDataException("The golden OCR sample manifest is invalid.");
        }
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException("Golden sample paths must be relative.");
        }

        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A golden sample path escapes the sample directory.");
        }

        return path;
    }

    private static bool SupportsLanguage(IEnumerable<string> supported, string requested) =>
        supported.Any(language =>
            language.Equals(requested, StringComparison.OrdinalIgnoreCase)
            || language.StartsWith(requested + '-', StringComparison.OrdinalIgnoreCase)
            || requested.StartsWith(language + '-', StringComparison.OrdinalIgnoreCase));
}
