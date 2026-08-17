using System.Globalization;
using System.Text;
using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.PpOcr;

public sealed class PpOcrV6SmallProvider : IOcrProvider
{
    private const int MaximumDetectedLines = 200;
    private const long MaximumPixelCount = 100_000_000;
    private readonly string _packageDirectory;
    private readonly IOcrImageDecoder _imageDecoder;
    private readonly IPpOcrV6InferenceRuntime _runtime;
    private readonly SemaphoreSlim _packageGate = new(1, 1);
    private ValidatedPpOcrModelPackage? _package;

    public PpOcrV6SmallProvider(
        string packageDirectory,
        IOcrImageDecoder imageDecoder,
        IPpOcrV6InferenceRuntime runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        _packageDirectory = Path.GetFullPath(packageDirectory);
        _imageDecoder = imageDecoder ?? throw new ArgumentNullException(nameof(imageDecoder));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public OcrProviderDescriptor Descriptor { get; private set; } = new(
        "paddle.pp-ocrv6-small",
        "PP-OCRv6 small",
        [],
        ["Hans", "Hant", "Jpan", "Latn"],
        SupportsMixedLanguages: true);

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetPackageAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    public async Task<OcrDocument> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatedPpOcrModelPackage package;
        try
        {
            package = await GetPackageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            throw new OcrProviderUnavailableException("ppocr.package-invalid");
        }

        if (request.LanguageHints.Count > 0
            && !request.LanguageHints.Any(hint => SupportsLanguage(package.Manifest.InputLanguages, hint)))
        {
            throw new OcrProviderUnavailableException("ppocr.language-unsupported");
        }

        try
        {
            await using var stream = await request.OpenImageAsync(cancellationToken).ConfigureAwait(false);
            var image = await _imageDecoder.DecodeAsync(stream, cancellationToken).ConfigureAwait(false);
            ValidateImage(image);
            var signature = package.Manifest.InputSignature;
            var detectionInput = CreateDetectionInput(image, signature.DetectionMaxSideLength);
            var detectionOutput = await _runtime.RunAsync(
                package.DetectionModelPath,
                signature.Detection.InputName,
                signature.Detection.OutputName,
                detectionInput.Values,
                [1, 3, detectionInput.Height, detectionInput.Width],
                cancellationToken).ConfigureAwait(false);
            var boxes = DetectTextBoxes(
                detectionOutput,
                image.Width,
                image.Height,
                signature.DetectionThreshold,
                signature.BoxThreshold);
            var lines = new List<OcrLine>(boxes.Count);
            foreach (var box in boxes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recognitionInput = CreateRecognitionInput(
                    image,
                    box,
                    signature.RecognitionHeight,
                    signature.RecognitionWidth);
                var recognitionOutput = await _runtime.RunAsync(
                    package.RecognitionModelPath,
                    signature.Recognition.InputName,
                    signature.Recognition.OutputName,
                    recognitionInput,
                    [1, 3, signature.RecognitionHeight, signature.RecognitionWidth],
                    cancellationToken).ConfigureAwait(false);
                var recognition = DecodeCtc(
                    recognitionOutput,
                    package.Dictionary,
                    signature.CtcBlankIndex);
                if (string.IsNullOrWhiteSpace(recognition.Text))
                {
                    continue;
                }

                lines.Add(new OcrLine(
                    recognition.Text,
                    box,
                    CreateWords(recognition.Text, box, recognition.Confidence),
                    recognition.Confidence));
            }

            var text = string.Join(Environment.NewLine, lines.Select(line => line.Text));
            var languages = DetectLanguageTags(text, request.LanguageHints);
            var warnings = new List<string>();
            if (lines.Count == MaximumDetectedLines)
            {
                warnings.Add("ppocr-detected-line-limit-reached");
            }

            if (languages.Count == 0)
            {
                languages.Add("und");
            }

            return new OcrDocument(
                text,
                lines,
                languages,
                warnings,
                new AnalysisProvenance(
                    Descriptor.ProviderId,
                    package.Manifest.Id,
                    package.Manifest.Version,
                    package.FileHashes,
                    package.Manifest.OutputSchemaVersion,
                    AnalysisExecutionLocation.Local,
                    AnalysisOutputKind.OcrFacts),
                image.Width,
                image.Height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new OcrProviderException("ppocr.invalid-model-output", isRetryable: false, exception);
        }
        catch (Exception exception)
        {
            throw new OcrProviderException("ppocr.inference-failed", isRetryable: true, exception);
        }
    }

    private async Task<ValidatedPpOcrModelPackage> GetPackageAsync(CancellationToken cancellationToken)
    {
        if (_package is not null)
        {
            return _package;
        }

        await _packageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_package is null)
            {
                _package = await PpOcrModelPackageValidator.ValidateAsync(
                    _packageDirectory,
                    cancellationToken).ConfigureAwait(false);
                Descriptor = Descriptor with
                {
                    SupportedLanguageTags = _package.Manifest.InputLanguages,
                    SupportedScripts = _package.Manifest.Scripts,
                    SupportsMixedLanguages = _package.Manifest.MixedLanguageSupport,
                };
            }

            return _package;
        }
        finally
        {
            _packageGate.Release();
        }
    }

    private static DetectionInput CreateDetectionInput(DecodedOcrImage image, int maximumSide)
    {
        var scale = Math.Min(1d, maximumSide / (double)Math.Max(image.Width, image.Height));
        var width = Math.Max(32, (int)Math.Round(image.Width * scale / 32d) * 32);
        var height = Math.Max(32, (int)Math.Round(image.Height * scale / 32d) * 32);
        var values = new float[checked(3 * width * height)];
        ResizeToNchw(image, 0, 0, image.Width, image.Height, values, width, height, padWithWhite: false);
        return new DetectionInput(values, width, height);
    }

    private static float[] CreateRecognitionInput(
        DecodedOcrImage image,
        OcrBoundingBox box,
        int targetHeight,
        int targetWidth)
    {
        var left = Math.Clamp((int)Math.Floor(box.X), 0, image.Width - 1);
        var top = Math.Clamp((int)Math.Floor(box.Y), 0, image.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(box.X + box.Width), left + 1, image.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(box.Y + box.Height), top + 1, image.Height);
        var cropWidth = right - left;
        var cropHeight = bottom - top;
        var contentWidth = Math.Clamp(
            (int)Math.Ceiling(targetHeight * (cropWidth / (double)cropHeight)),
            1,
            targetWidth);
        var values = new float[checked(3 * targetWidth * targetHeight)];
        Array.Fill(values, 1f);
        ResizeToNchw(
            image,
            left,
            top,
            cropWidth,
            cropHeight,
            values,
            contentWidth,
            targetHeight,
            targetWidth);
        return values;
    }

    private static void ResizeToNchw(
        DecodedOcrImage image,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        float[] destination,
        int contentWidth,
        int contentHeight,
        int? destinationStride = null,
        bool padWithWhite = true)
    {
        var stride = destinationStride ?? contentWidth;
        if (padWithWhite && destination.All(value => value == 0))
        {
            Array.Fill(destination, 1f);
        }

        var planeLength = checked(stride * contentHeight);
        for (var y = 0; y < contentHeight; y++)
        {
            var sourcePixelY = sourceY + Math.Min(sourceHeight - 1, (int)((y + 0.5) * sourceHeight / contentHeight));
            for (var x = 0; x < contentWidth; x++)
            {
                var sourcePixelX = sourceX + Math.Min(sourceWidth - 1, (int)((x + 0.5) * sourceWidth / contentWidth));
                var sourceIndex = checked((sourcePixelY * image.Width + sourcePixelX) * 4);
                var destinationIndex = y * stride + x;
                destination[destinationIndex] = Normalize(image.RgbaPixels[sourceIndex]);
                destination[planeLength + destinationIndex] = Normalize(image.RgbaPixels[sourceIndex + 1]);
                destination[(2 * planeLength) + destinationIndex] = Normalize(image.RgbaPixels[sourceIndex + 2]);
            }
        }
    }

    private static List<OcrBoundingBox> DetectTextBoxes(
        OcrTensorResult output,
        int imageWidth,
        int imageHeight,
        float detectionThreshold,
        float boxThreshold)
    {
        if (output.Dimensions.Count < 2)
        {
            throw new InvalidDataException("The detection output rank is invalid.");
        }

        var mapHeight = output.Dimensions[^2];
        var mapWidth = output.Dimensions[^1];
        if (mapHeight <= 0 || mapWidth <= 0 || output.Values.Length < checked(mapHeight * mapWidth))
        {
            throw new InvalidDataException("The detection output shape is invalid.");
        }

        var offset = output.Values.Length - checked(mapHeight * mapWidth);
        var mask = new bool[checked(mapHeight * mapWidth)];
        for (var index = 0; index < mask.Length; index++)
        {
            mask[index] = output.Values[offset + index] >= detectionThreshold;
        }

        var visited = new bool[mask.Length];
        var components = new List<MapBox>();
        var queue = new Queue<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || visited[start])
            {
                continue;
            }

            visited[start] = true;
            queue.Enqueue(start);
            var minX = mapWidth;
            var minY = mapHeight;
            var maxX = 0;
            var maxY = 0;
            var count = 0;
            var score = 0d;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var y = current / mapWidth;
                var x = current - (y * mapWidth);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                score += output.Values[offset + current];
                count++;
                Enqueue(x - 1, y, mapWidth, mapHeight, mask, visited, queue);
                Enqueue(x + 1, y, mapWidth, mapHeight, mask, visited, queue);
                Enqueue(x, y - 1, mapWidth, mapHeight, mask, visited, queue);
                Enqueue(x, y + 1, mapWidth, mapHeight, mask, visited, queue);
            }

            if (count >= 3 && score / count >= boxThreshold)
            {
                components.Add(new MapBox(minX, minY, maxX + 1, maxY + 1, score / count));
            }
        }

        var merged = MergeComponents(components);
        return merged
            .OrderBy(box => box.Top)
            .ThenBy(box => box.Left)
            .Take(MaximumDetectedLines)
            .Select(box =>
            {
                var xScale = imageWidth / (double)mapWidth;
                var yScale = imageHeight / (double)mapHeight;
                var expansionX = Math.Max(1d, box.Width * xScale * 0.03);
                var expansionY = Math.Max(1d, box.Height * yScale * 0.08);
                var left = Math.Max(0d, (box.Left * xScale) - expansionX);
                var top = Math.Max(0d, (box.Top * yScale) - expansionY);
                var right = Math.Min(imageWidth, (box.Right * xScale) + expansionX);
                var bottom = Math.Min(imageHeight, (box.Bottom * yScale) + expansionY);
                return new OcrBoundingBox(left, top, right - left, bottom - top);
            })
            .Where(box => box.Width >= 2 && box.Height >= 2)
            .ToList();
    }

    private static List<MapBox> MergeComponents(List<MapBox> components)
    {
        var ordered = components.OrderBy(box => box.Top).ThenBy(box => box.Left).ToList();
        var result = new List<MapBox>();
        foreach (var component in ordered)
        {
            var mergeIndex = result.FindLastIndex(existing =>
            {
                var overlap = Math.Min(existing.Bottom, component.Bottom) - Math.Max(existing.Top, component.Top);
                var minimumHeight = Math.Min(existing.Height, component.Height);
                var gap = component.Left - existing.Right;
                return overlap > minimumHeight * 0.45
                    && gap <= Math.Max(existing.Height * 1.5, 12)
                    && gap >= -Math.Max(existing.Height, component.Height);
            });
            if (mergeIndex < 0)
            {
                result.Add(component);
            }
            else
            {
                result[mergeIndex] = MapBox.Union(result[mergeIndex], component);
            }
        }

        return result;
    }

    private static void Enqueue(
        int x,
        int y,
        int width,
        int height,
        bool[] mask,
        bool[] visited,
        Queue<int> queue)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var index = y * width + x;
        if (mask[index] && !visited[index])
        {
            visited[index] = true;
            queue.Enqueue(index);
        }
    }

    private static RecognitionResult DecodeCtc(
        OcrTensorResult output,
        IReadOnlyList<string> dictionary,
        int blankIndex)
    {
        if (output.Dimensions.Count < 2)
        {
            throw new InvalidDataException("The recognition output rank is invalid.");
        }

        var classCount = output.Dimensions[^1];
        var timeSteps = output.Dimensions[^2];
        if (classCount <= 1 || timeSteps <= 0 || output.Values.Length < checked(classCount * timeSteps))
        {
            throw new InvalidDataException("The recognition output shape is invalid.");
        }

        var offset = output.Values.Length - checked(classCount * timeSteps);
        var builder = new StringBuilder();
        var previous = -1;
        var probabilities = new List<double>();
        for (var step = 0; step < timeSteps; step++)
        {
            var rowStart = offset + (step * classCount);
            var selected = 0;
            var maximum = output.Values[rowStart];
            for (var label = 1; label < classCount; label++)
            {
                var value = output.Values[rowStart + label];
                if (value > maximum)
                {
                    maximum = value;
                    selected = label;
                }
            }

            if (selected != blankIndex && selected != previous)
            {
                var dictionaryIndex = selected > blankIndex ? selected - 1 : selected;
                if (dictionaryIndex >= 0 && dictionaryIndex < dictionary.Count)
                {
                    builder.Append(dictionary[dictionaryIndex]);
                    probabilities.Add(SoftmaxProbability(output.Values, rowStart, classCount, selected, maximum));
                }
            }

            previous = selected;
        }

        return new RecognitionResult(
            builder.ToString(),
            probabilities.Count == 0 ? null : probabilities.Average());
    }

    private static double SoftmaxProbability(
        float[] values,
        int offset,
        int count,
        int selected,
        float maximum)
    {
        var denominator = 0d;
        for (var index = 0; index < count; index++)
        {
            denominator += Math.Exp(values[offset + index] - maximum);
        }

        return denominator <= 0
            ? 0
            : Math.Exp(values[offset + selected] - maximum) / denominator;
    }

    private static IReadOnlyList<OcrWord> CreateWords(
        string text,
        OcrBoundingBox lineBox,
        double? confidence)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            return [new OcrWord(text, lineBox, confidence)];
        }

        var totalElements = Math.Max(1, parts.Sum(part => new StringInfo(part).LengthInTextElements));
        var result = new List<OcrWord>(parts.Length);
        var consumed = 0;
        foreach (var part in parts)
        {
            var elements = new StringInfo(part).LengthInTextElements;
            var x = lineBox.X + (lineBox.Width * consumed / totalElements);
            var width = lineBox.Width * elements / totalElements;
            result.Add(new OcrWord(part, new OcrBoundingBox(x, lineBox.Y, width, lineBox.Height), confidence));
            consumed += elements;
        }

        return result;
    }

    private static List<string> DetectLanguageTags(string text, IReadOnlyList<string> hints)
    {
        var result = new List<string>();
        var hasHan = false;
        var hasJapaneseKana = false;
        var hasLatin = false;
        var hasArabic = false;
        var hasThai = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            hasHan |= value is >= 0x3400 and <= 0x9FFF;
            hasJapaneseKana |= value is >= 0x3040 and <= 0x30FF;
            hasLatin |= value is >= 0x0041 and <= 0x024F;
            hasArabic |= value is >= 0x0600 and <= 0x06FF;
            hasThai |= value is >= 0x0E00 and <= 0x0E7F;
        }

        if (hasJapaneseKana)
        {
            result.Add("ja");
        }
        else if (hasHan)
        {
            result.Add(hints.FirstOrDefault(hint => hint.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ?? "und-Hani");
        }

        if (hasLatin)
        {
            result.Add(hints.FirstOrDefault(hint =>
                !hint.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                && !hint.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) ?? "und-Latn");
        }

        if (hasArabic)
        {
            result.Add("und-Arab");
        }

        if (hasThai)
        {
            result.Add("th");
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool SupportsLanguage(IEnumerable<string> supported, string hint) =>
        supported.Any(language =>
            language.Equals(hint, StringComparison.OrdinalIgnoreCase)
            || language.StartsWith(hint + '-', StringComparison.OrdinalIgnoreCase)
            || hint.StartsWith(language + '-', StringComparison.OrdinalIgnoreCase));

    private static void ValidateImage(DecodedOcrImage image)
    {
        if (image.Width <= 0
            || image.Height <= 0
            || (long)image.Width * image.Height > MaximumPixelCount
            || image.RgbaPixels.Length != checked(image.Width * image.Height * 4))
        {
            throw new InvalidDataException("The decoded OCR image is invalid.");
        }
    }

    private static float Normalize(byte value) => (value / 127.5f) - 1f;

    private sealed record DetectionInput(float[] Values, int Width, int Height);

    private sealed record RecognitionResult(string Text, double? Confidence);

    private sealed record MapBox(int Left, int Top, int Right, int Bottom, double Score)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;

        public static MapBox Union(MapBox left, MapBox right)
        {
            var totalArea = Math.Max(1, (left.Width * left.Height) + (right.Width * right.Height));
            var score = ((left.Score * left.Width * left.Height) + (right.Score * right.Width * right.Height)) / totalArea;
            return new MapBox(
                Math.Min(left.Left, right.Left),
                Math.Min(left.Top, right.Top),
                Math.Max(left.Right, right.Right),
                Math.Max(left.Bottom, right.Bottom),
                score);
        }
    }
}
