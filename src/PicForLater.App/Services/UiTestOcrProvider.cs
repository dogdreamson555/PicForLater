#if PICFORLATER_UI_TESTING
using System.Globalization;
using PicForLater.Core.Analysis;

namespace PicForLater.App.Services;

public sealed class UiTestOcrProvider : IOcrProvider
{
#if PICFORLATER_UI_VISUAL_FIXTURE
    private const string VisualFixtureFilePrefix = "visual-dense-";
#endif
    private const string DifferentTimesRegressionFileName = "different-times-candidates.png";
    private const string PastDateRegressionFileName = "past-date-candidate.png";
    private const string PartialDateRegressionFileName = "partial-date-candidate.png";
    private const string RealOcrRegressionFileName = "real-ocr-natural-language.png";
    private const string SplitDateTimeRegressionFileName = "split-date-time-candidate.png";
    private readonly WindowsMediaOcrProvider _windowsOcr = new();

    public OcrProviderDescriptor Descriptor { get; } = new(
        "local.ui-test-ocr",
        "UI test OCR",
        ["zh-Hans", "zh-Hant", "ar", "th", "ja", "vi", "en"],
        ["Hans", "Hant", "Arab", "Thai", "Jpan", "Latn"],
        SupportsMixedLanguages: true);

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);

    public async Task<OcrDocument> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if PICFORLATER_UI_VISUAL_FIXTURE
        if (request.OriginalFileName.StartsWith(
                VisualFixtureFilePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return CreateVisualFixtureDocument(request);
        }
#endif
        if (request.OriginalFileName.Equals(
                RealOcrRegressionFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            // This UI-test-only route exercises the real local OCR stack against the
            // problem regression image. Re-label its recognized scripts the same way
            // the multilingual PP-OCR provider does so the downstream date router also
            // covers und-Hani/und-Latn rather than only Windows' language tag.
            var document = await _windowsOcr.RecognizeAsync(request, cancellationToken);
            return document with
            {
                LanguageTags = ["und-Hani", "und-Latn"],
            };
        }

        if (request.OriginalFileName.Equals(
                PartialDateRegressionFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            const string partialDateLine = "年度总决赛&颁奖日期（预计）：2027年5月";
            OcrLine[] partialDateLines =
            [
                new(
                    partialDateLine,
                    new OcrBoundingBox(0.08, 0.12, 0.72, 0.08),
                    [],
                    0.99),
            ];
            return new OcrDocument(
                partialDateLine,
                partialDateLines,
                ["zh-Hans"],
                [],
                new AnalysisProvenance(
                    Descriptor.ProviderId,
                    ModelId: null,
                    ModelVersion: null,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    "ui-test-ocr.v1",
                    AnalysisExecutionLocation.Local,
                    AnalysisOutputKind.OcrFacts),
                request.PixelWidth,
                request.PixelHeight);
        }

        if (request.OriginalFileName.Equals(
                PastDateRegressionFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            var past = DateTimeOffset.Now.AddDays(-30);
            var pastLine = string.Format(
                CultureInfo.InvariantCulture,
                "帖子发布于 {0:yyyy/M/d H:mm}",
                past);
            return CreateDocument(
                request,
                [
                    new OcrLine(
                        pastLine,
                        new OcrBoundingBox(0.08, 0.12, 0.72, 0.08),
                        [],
                        0.99),
                ]);
        }

        if (request.OriginalFileName.Equals(
                SplitDateTimeRegressionFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            var future = DateTimeOffset.Now.AddDays(30);
            var splitDateLine = string.Format(
                CultureInfo.InvariantCulture,
                "申报截止时间：{0:yyyy.M.d}（周",
                future);
            const string timeLine = "五）16:30";
            var duplicateLine = string.Format(
                CultureInfo.InvariantCulture,
                "截止时间摘要：{0:yyyy.M.d} 16:30",
                future);
            return CreateDocument(
                request,
                [
                    new OcrLine(
                        splitDateLine,
                        new OcrBoundingBox(0.08, 0.12, 0.72, 0.08),
                        [],
                        0.99),
                    new OcrLine(
                        timeLine,
                        new OcrBoundingBox(0.08, 0.22, 0.28, 0.08),
                        [],
                        0.99),
                    new OcrLine(
                        duplicateLine,
                        new OcrBoundingBox(0.08, 0.36, 0.72, 0.08),
                        [],
                        0.99),
                ]);
        }

        if (request.OriginalFileName.Equals(
                DifferentTimesRegressionFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            var future = DateTimeOffset.Now.AddDays(31);
            var firstLine = string.Format(
                CultureInfo.InvariantCulture,
                "第一场：{0:yyyy.M.d} 14:30",
                future);
            var secondLine = string.Format(
                CultureInfo.InvariantCulture,
                "第二场：{0:yyyy.M.d} 16:30",
                future);
            return CreateDocument(
                request,
                [
                    new OcrLine(
                        firstLine,
                        new OcrBoundingBox(0.08, 0.12, 0.72, 0.08),
                        [],
                        0.99),
                    new OcrLine(
                        secondLine,
                        new OcrBoundingBox(0.08, 0.28, 0.72, 0.08),
                        [],
                        0.99),
                ]);
        }

        var eventDate = DateTimeOffset.Now.AddDays(30);
        var dateLine = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy}年{0:MM}月{0:dd}日 14:30",
            eventDate);
        const string locationLine = "上海市浦东新区世纪大道100号";
        OcrLine[] lines =
        [
            new(
                dateLine,
                new OcrBoundingBox(0.08, 0.12, 0.72, 0.08),
                [],
                0.99),
            new(
                locationLine,
                new OcrBoundingBox(0.08, 0.24, 0.72, 0.08),
                [],
                0.99),
        ];
        return new OcrDocument(
            string.Join(Environment.NewLine, lines.Select(line => line.Text)),
            lines,
            ["zh-Hans"],
            [],
            new AnalysisProvenance(
                Descriptor.ProviderId,
                ModelId: null,
                ModelVersion: null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "ui-test-ocr.v1",
                AnalysisExecutionLocation.Local,
                AnalysisOutputKind.OcrFacts),
            request.PixelWidth,
            request.PixelHeight);
    }

    private OcrDocument CreateDocument(
        OcrRequest request,
        IReadOnlyList<OcrLine> lines,
        IReadOnlyList<string>? languageTags = null) =>
        new(
            string.Join(Environment.NewLine, lines.Select(line => line.Text)),
            lines,
            languageTags ?? ["zh-Hans"],
            [],
            new AnalysisProvenance(
                Descriptor.ProviderId,
                ModelId: null,
                ModelVersion: null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                "ui-test-ocr.v1",
                AnalysisExecutionLocation.Local,
                AnalysisOutputKind.OcrFacts),
            request.PixelWidth,
            request.PixelHeight);

#if PICFORLATER_UI_VISUAL_FIXTURE
    private OcrDocument CreateVisualFixtureDocument(OcrRequest request)
    {
        var (languageTags, textLines) = request.OriginalFileName.ToLowerInvariant() switch
        {
            "visual-dense-01-city-walk.png" =>
                ((IReadOnlyList<string>)["zh-Hans", "en"],
                    (IReadOnlyList<string>)[
                        "周末城市散步路线 City Walk",
                        "滨江步道、旧书店与咖啡馆，适合保存后慢慢查看。",
                    ]),
            "visual-dense-02-travel-plan.png" =>
                (["en"],
                    [
                        "Northern coast travel notes",
                        "A compact itinerary with train, museum and sunset viewpoints.",
                    ]),
            "visual-dense-03-design-review.png" =>
                (["en"],
                    [
                        "Design review · 2099-05-18 09:30",
                        "Room Atlas, product workspace",
                    ]),
            "visual-dense-04-project-retro.png" =>
                (["zh-Hans"],
                    [
                        "项目复盘会议 2099年6月20日 14:00",
                        "上海市浦东新区世纪大道100号会议室",
                    ]),
            "visual-dense-05-book-list.png" =>
                (["zh-Hant"],
                    [
                        "今年想慢慢讀完的書單",
                        "設計、城市、歷史與幾本適合旅途中閱讀的小說。",
                    ]),
            "visual-dense-06-mixed-language.png" =>
                (["zh-Hans", "en"],
                    [
                        "研究素材 Research snippets",
                        "混合语言长标题用于检查卡片换行、截断和集合密度的一致性。",
                    ]),
            "visual-dense-07-arabic-note.png" =>
                (["ar"],
                    [
                        "ملاحظات عن معرض التصميم والمدينة",
                        "نص طويل لاختبار اتجاه الكتابة والتفاف العنوان داخل البطاقة.",
                    ]),
            "visual-dense-08-thai-recipe.png" =>
                (["th"],
                    [
                        "สูตรอาหารที่อยากลองทำในวันหยุด",
                        "ข้อความภาษาไทยแบบไม่มีช่องว่างเพื่อทดสอบการตัดบรรทัด",
                    ]),
            "visual-dense-09-emoji-board.png" =>
                (["zh-Hans", "en"],
                    [
                        "灵感板 ✨ Café + Typography",
                        "组合字符 café, naïve, é 与 emoji 用于 Unicode 视觉回归。",
                    ]),
            "visual-dense-10-japanese-poster.png" =>
                (["ja"],
                    [
                        "街の写真展ポスター",
                        "横長の画像と日本語テキストを確認するためのサンプルです。",
                    ]),
            "visual-dense-11-vietnamese-receipt.png" =>
                (["vi"],
                    [
                        "Danh sách đồ dùng cho chuyến đi",
                        "Tiêu đề và mô tả có dấu để kiểm tra phông chữ và xuống dòng.",
                    ]),
            "visual-dense-12-photo-reference.png" =>
                (["en"],
                    [
                        "Quiet architecture reference",
                        "Window light, concrete texture and a strong landscape crop.",
                    ]),
            "visual-dense-13-recycle-portrait.png" =>
                (["zh-Hans"], ["已删除的竖版活动海报", "回收站密集态视觉样本。"]),
            "visual-dense-14-recycle-landscape.png" =>
                (["en"], ["Archived landscape reference", "Recycle Bin visual fixture."]),
            "visual-dense-15-recycle-ultrawide.png" =>
                (["zh-Hant"], ["已刪除的超寬行程截圖", "測試回收站卡片裁切。"]),
            "visual-dense-16-recycle-square.png" =>
                (["zh-Hans", "en"], ["已删除的方形灵感图", "Square recycle fixture."]),
            _ => throw new InvalidDataException(
                $"Unknown {UiTestVisualFixtureSeeder.FixtureId} OCR fixture file: " +
                request.OriginalFileName),
        };

        var lines = textLines
            .Select((text, index) => new OcrLine(
                text,
                new OcrBoundingBox(0.06, 0.10 + (index * 0.14), 0.86, 0.10),
                [],
                0.99))
            .ToArray();
        return CreateDocument(request, lines, languageTags);
    }
#endif
}
#endif
