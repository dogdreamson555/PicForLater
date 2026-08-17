using System.Runtime.InteropServices.WindowsRuntime;
using PicForLater.Core.Analysis;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PicForLater.App.Services;

public sealed class WindowsMediaOcrProvider : IOcrProvider
{
    public WindowsMediaOcrProvider()
    {
        Descriptor = CreateDescriptor();
    }

    public OcrProviderDescriptor Descriptor { get; private set; }

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Descriptor = CreateDescriptor();
        return ValueTask.FromResult(Descriptor.SupportedLanguageTags.Count > 0);
    }

    public async Task<OcrDocument> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var engine = CreateEngine(request.LanguageHints)
            ?? throw new OcrProviderUnavailableException("windows-ocr.language-pack-missing");
        await using var source = await request.OpenImageAsync(cancellationToken).ConfigureAwait(false);
        if (source.CanSeek)
        {
            source.Position = 0;
        }

        using var randomAccessStream = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken);
        var sourceWidth = decoder.OrientedPixelWidth;
        var sourceHeight = decoder.OrientedPixelHeight;
        if (sourceWidth == 0 || sourceHeight == 0)
        {
            throw new OcrProviderException("windows-ocr.invalid-image", isRetryable: false);
        }

        var scale = Math.Min(
            1d,
            OcrEngine.MaxImageDimension / (double)Math.Max(sourceWidth, sourceHeight));
        var width = Math.Max(1u, checked((uint)Math.Round(sourceWidth * scale)));
        var height = Math.Max(1u, checked((uint)Math.Round(sourceHeight * scale)));
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        SoftwareBitmap bitmap;
        try
        {
            bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OcrProviderException(
                "windows-ocr.bitmap-conversion-failed",
                isRetryable: false,
                exception);
        }

        using (bitmap)
        {
            Windows.Media.Ocr.OcrResult result;
            try
            {
                result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new OcrProviderException(
                    "windows-ocr.recognition-failed",
                    isRetryable: false,
                    exception);
            }

            return CreateDocument(result, engine, width, height);
        }
    }

    private OcrDocument CreateDocument(
        Windows.Media.Ocr.OcrResult result,
        OcrEngine engine,
        uint width,
        uint height)
    {
        var lines = result.Lines.Select(line =>
        {
            var words = line.Words.Select(word =>
                new PicForLater.Core.Analysis.OcrWord(
                    word.Text,
                    new OcrBoundingBox(
                        word.BoundingRect.X,
                        word.BoundingRect.Y,
                        word.BoundingRect.Width,
                        word.BoundingRect.Height),
                    Confidence: null)).ToArray();
            return new PicForLater.Core.Analysis.OcrLine(
                line.Text,
                Union(words.Select(word => word.BoundingBox)),
                words,
                Confidence: null);
        }).ToArray();
        return new OcrDocument(
            result.Text,
            lines,
            [engine.RecognizerLanguage.LanguageTag],
            [],
            new AnalysisProvenance(
                Descriptor.ProviderId,
                engine.RecognizerLanguage.LanguageTag,
                Environment.OSVersion.Version.ToString(),
                new Dictionary<string, string>(StringComparer.Ordinal),
                "windows-media-ocr.v1",
                AnalysisExecutionLocation.Local,
                AnalysisOutputKind.OcrFacts),
            checked((int)width),
            checked((int)height));
    }

    private static OcrEngine? CreateEngine(IReadOnlyList<string> languageHints)
    {
        var languages = OcrEngine.AvailableRecognizerLanguages;
        if (languageHints.Count > 0)
        {
            foreach (var hint in languageHints)
            {
                var match = languages.FirstOrDefault(language => LanguageMatches(language.LanguageTag, hint));
                if (match is not null)
                {
                    return OcrEngine.TryCreateFromLanguage(match);
                }
            }

            return null;
        }

        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? languages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(engine => engine is not null);
    }

    private static OcrProviderDescriptor CreateDescriptor()
    {
        var languages = OcrEngine.AvailableRecognizerLanguages
            .Select(language => language.LanguageTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new OcrProviderDescriptor(
            "windows.media-ocr",
            "Windows OCR",
            languages,
            [],
            SupportsMixedLanguages: false);
    }

    private static bool LanguageMatches(string installed, string requested) =>
        installed.Equals(requested, StringComparison.OrdinalIgnoreCase)
        || installed.StartsWith(requested + '-', StringComparison.OrdinalIgnoreCase)
        || requested.StartsWith(installed + '-', StringComparison.OrdinalIgnoreCase);

    private static OcrBoundingBox Union(IEnumerable<OcrBoundingBox> boxes)
    {
        var values = boxes.ToArray();
        if (values.Length == 0)
        {
            return new OcrBoundingBox(0, 0, 0, 0);
        }

        var left = values.Min(box => box.X);
        var top = values.Min(box => box.Y);
        var right = values.Max(box => box.X + box.Width);
        var bottom = values.Max(box => box.Y + box.Height);
        return new OcrBoundingBox(left, top, right - left, bottom - top);
    }
}
