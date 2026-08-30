#if PICFORLATER_UI_VISUAL_FIXTURE
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Core.Reminders;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.App.Services;

/// <summary>
/// Builds the isolated, deterministic data set used by the dense-v1 screenshot matrix.
/// This type is compiled only for the opt-in UiTest visual-fixture build.
/// </summary>
internal static class UiTestVisualFixtureSeeder
{
    internal const string FixtureId = "dense-v1";

    private static readonly DateTimeOffset FixtureNowUtc =
        new(2032, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly FixtureModelPackage[] ModelPackages =
    [
        new(
            "fixture.pp-ocrv6-small@6.0.0",
            new ModelPackageManifest(
                1,
                "pp-ocrv6-small",
                "6.0.0",
                "onnxruntime",
                "onnx",
                "multilingual-ocr",
                "fp32",
                [ModelCapability.Ocr],
                ["en", "ja", "zh-Hans", "zh-Hant"],
                ["en", "ja", "zh-Hans", "zh-Hant"],
                ["Hans", "Hant", "Jpan", "Latn"],
                true,
                [],
                "Apache-2.0",
                "https://www.paddleocr.ai/",
                31_190_469,
                31_190_469,
                2L * 1024 * 1024 * 1024,
                "2 GiB RAM; CPU baseline",
                "ppocr.image.v1",
                "picforlater.ocr.v1",
                ["CPU"])),
        new(
            "fixture.qwen3-vl-2b-instruct-q4f32-cpu@0.2.0",
            CreateFixtureQwenManifest(
                "0.2.0-cpu-visual-fixture",
                "fp32-int4",
                2_420_000_000,
                8L * 1024 * 1024 * 1024,
                "8 GiB RAM; modern x64 CPU",
                "CPU")),
        new(
            "fixture.qwen3-vl-2b-instruct-q4f16-cuda@0.2.0",
            CreateFixtureQwenManifest(
                "0.2.0-cuda-visual-fixture",
                "int4",
                1_860_000_000,
                8L * 1024 * 1024 * 1024,
                "NVIDIA GPU with 6 GiB VRAM",
                "CUDA")),
    ];

    private static readonly FixtureItem[] Items =
    [
        new(
            "visual-dense-01-city-walk.png",
            960,
            540,
            11,
            ImageSourceKind.File,
            "周末城市散步路线 City Walk",
            "保存滨江步道、旧书店和咖啡馆的路线，周末带上相机慢慢走，也用于检查中文与英文混排。",
            AnalysisState.Completed,
            IsDeleted: false,
            ["旅行", "灵感"]),
        new(
            "visual-dense-02-travel-plan.png",
            540,
            960,
            23,
            ImageSourceKind.File,
            "Northern coast travel notes",
            "Train times, museum stops, a tiny seafood restaurant, and two sunset viewpoints saved for a later trip.",
            AnalysisState.Running,
            IsDeleted: false,
            ["旅行"]),
        new(
            "visual-dense-03-design-review.png",
            1200,
            320,
            37,
            ImageSourceKind.File,
            "Design review · compact header and very wide reference image",
            "A deliberately long English summary for testing dense cards, metadata alignment, and truncation at compact window widths.",
            AnalysisState.NeedsAttention,
            IsDeleted: false,
            ["工作"]),
        new(
            "visual-dense-04-project-retro.png",
            720,
            960,
            41,
            ImageSourceKind.File,
            "项目复盘会议：跨团队交付、风险与下一阶段行动项",
            "包含明确日期、时间和地点的长标题样本，用于提醒候选与卡片多行排版回归。",
            AnalysisState.Pending,
            IsDeleted: false,
            ["工作"]),
        new(
            "visual-dense-05-book-list.png",
            720,
            720,
            53,
            ImageSourceKind.File,
            "今年想慢慢讀完的書單",
            "設計、城市、歷史與幾本適合旅途中閱讀的小說。",
            AnalysisState.Completed,
            IsDeleted: false,
            ["阅读"]),
        new(
            "visual-dense-06-mixed-language.png",
            1100,
            620,
            67,
            ImageSourceKind.File,
            "研究素材 Research snippets — 混合语言长标题与多行简介",
            "保留术语原文、Unicode 标点和英文缩写；这是一条为了验证高密度资料库布局而写得稍长的简介。",
            AnalysisState.Completed,
            IsDeleted: false,
            ["工作", "灵感"]),
        new(
            "visual-dense-07-arabic-note.png",
            420,
            1000,
            79,
            ImageSourceKind.File,
            "ملاحظات عن معرض التصميم والمدينة",
            "نص طويل لاختبار اتجاه الكتابة والتفاف العنوان داخل البطاقة مع بيانات متعددة اللغات.",
            AnalysisState.Completed,
            IsDeleted: false,
            ["灵感"]),
        new(
            "visual-dense-08-thai-recipe.png",
            1000,
            420,
            83,
            ImageSourceKind.File,
            "สูตรอาหารที่อยากลองทำในวันหยุด",
            "ข้อความภาษาไทยแบบไม่มีช่องว่างเพื่อทดสอบการตัดบรรทัดและความหนาแน่นของการ์ด",
            AnalysisState.Pending,
            IsDeleted: false,
            ["灵感"]),
        new(
            "visual-dense-09-emoji-board.png",
            800,
            800,
            97,
            ImageSourceKind.Clipboard,
            "灵感板 ✨ Café + Typography",
            "组合字符 café、naïve、é 与 emoji 🧭 用于 Unicode、剪贴板来源和方形缩略图视觉回归。",
            AnalysisState.Completed,
            IsDeleted: false,
            ["灵感"]),
        new(
            "visual-dense-10-japanese-poster.png",
            1280,
            360,
            101,
            ImageSourceKind.File,
            "街の写真展ポスター",
            "横長の画像と日本語テキストを確認するためのサンプルです。",
            AnalysisState.Running,
            IsDeleted: false,
            ["旅行"]),
        new(
            "visual-dense-11-vietnamese-receipt.png",
            640,
            960,
            113,
            ImageSourceKind.File,
            "Danh sách đồ dùng cho chuyến đi",
            "Tiêu đề và mô tả có dấu để kiểm tra phông chữ, xuống dòng và trạng thái cần xử lý.",
            AnalysisState.NeedsAttention,
            IsDeleted: false,
            ["旅行"]),
        new(
            "visual-dense-12-photo-reference.png",
            1024,
            600,
            127,
            ImageSourceKind.File,
            "Quiet architecture reference",
            "Window light, concrete texture, and a strong landscape crop.",
            AnalysisState.Completed,
            IsDeleted: false,
            ["灵感"]),
        new(
            "visual-dense-13-recycle-portrait.png",
            600,
            900,
            131,
            ImageSourceKind.File,
            "已删除的竖版活动海报",
            "回收站密集态视觉样本，可恢复但不会出现在资料库中。",
            AnalysisState.Completed,
            IsDeleted: true,
            ["工作"]),
        new(
            "visual-dense-14-recycle-landscape.png",
            900,
            600,
            149,
            ImageSourceKind.File,
            "Archived landscape reference",
            "A deleted wide image used to verify recycle-bin crop and metadata rhythm.",
            AnalysisState.NeedsAttention,
            IsDeleted: true,
            ["旅行"]),
        new(
            "visual-dense-15-recycle-ultrawide.png",
            1100,
            280,
            157,
            ImageSourceKind.File,
            "已刪除的超寬行程截圖",
            "測試回收站卡片裁切、繁體中文排版與不同寬高比。",
            AnalysisState.Running,
            IsDeleted: true,
            ["旅行"]),
        new(
            "visual-dense-16-recycle-square.png",
            720,
            720,
            173,
            ImageSourceKind.Clipboard,
            "已删除的方形灵感图",
            "Square recycle fixture with a clipboard source and pending state.",
            AnalysisState.Pending,
            IsDeleted: true,
            []),
    ];

    internal static TimeProvider Clock { get; } = new FrozenTimeProvider(FixtureNowUtc);

    private static TimeZoneInfo DisplayTimeZone { get; } =
        TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    internal static void ConfigureProcessCulture()
    {
        const string languageTag = "zh-CN";
        var culture = CultureInfo.GetCultureInfo(languageTag);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    internal static string FormatDisplayTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, DisplayTimeZone)
            .ToString("g", CultureInfo.CurrentCulture);

    internal static async Task SeedAsync(
        AppDataPaths paths,
        IImageImportService imageImporter,
        ILibraryService library,
        IReminderService reminders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(imageImporter);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(reminders);

        await EnsureFixtureModelPackagesAsync(paths, cancellationToken).ConfigureAwait(false);

        // A storage retry can call the initializer again in the same process. Never
        // import on top of an existing fixture: a previously completed fixture is
        // validated and reused, while any partial or foreign shape fails immediately.
        // The latter deliberately requires a fresh isolated UI-test data root instead
        // of turning a failed attempt into duplicate assets and analysis jobs.
        if (await TryUseExistingFixtureAsync(library, reminders, cancellationToken)
                .ConfigureAwait(false))
        {
            await ValidateFixtureModelPackagesAsync(paths, cancellationToken).ConfigureAwait(false);
            return;
        }

        var imageItemIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            await using var image = CreatePng(item.PixelWidth, item.PixelHeight, item.PatternSeed);
            var import = await imageImporter.ImportAsync(
                    image,
                    item.FileName,
                    item.SourceKind,
                    ManagedImageFormat.Png,
                    cancellationToken)
                .ConfigureAwait(false);
            imageItemIds[item.FileName] = import.ImageItemId;
        }

        await WaitForCompletedAnalysisAsync(library, imageItemIds.Values, cancellationToken)
            .ConfigureAwait(false);

        var categories = await EnsureCategoriesAsync(library, cancellationToken)
            .ConfigureAwait(false);
        foreach (var item in Items)
        {
            var imageItemId = imageItemIds[item.FileName];
            await library.UpdateUserFieldsAsync(
                    imageItemId,
                    item.Title,
                    item.Summary,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var categoryName in item.CategoryNames)
            {
                await library.SetCategoryAssignmentAsync(
                        imageItemId,
                        categories[categoryName].Id,
                        isAssigned: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await NormalizeRowsAndSeedCandidatesAsync(
                paths,
                imageItemIds,
                includeDeletedAt: false,
                cancellationToken)
            .ConfigureAwait(false);
        await EnsureRemindersAsync(reminders, imageItemIds, cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in Items.Where(candidate => candidate.IsDeleted))
        {
            var imageItemId = imageItemIds[item.FileName];
            var existing = await library.GetAsync(imageItemId, cancellationToken).ConfigureAwait(false);
            if (existing?.Item.DeletedAtUtc is null)
            {
                await library.SoftDeleteAsync(imageItemId, cancellationToken).ConfigureAwait(false);
            }
        }

        // Service operations intentionally use their production timestamps. Normalize the
        // presentation rows only after those operations so screenshot ordering never drifts.
        await NormalizeRowsAndSeedCandidatesAsync(
                paths,
                imageItemIds,
                includeDeletedAt: true,
                cancellationToken)
            .ConfigureAwait(false);
        await WaitForStableReminderOutboxAsync(reminders, cancellationToken).ConfigureAwait(false);
        await ValidateAsync(library, reminders, cancellationToken).ConfigureAwait(false);
        await ValidateFixtureModelPackagesAsync(paths, cancellationToken).ConfigureAwait(false);
    }

    private static ModelPackageManifest CreateFixtureQwenManifest(
        string version,
        string quantization,
        long installedBytes,
        long minRamBytes,
        string recommendedHardware,
        string executionProvider) => new(
            1,
            "picforlater.qwen3-vl-2b-instruct",
            version,
            "onnxruntime-genai",
            "onnx",
            "qwen3-vl-2b-instruct",
            quantization,
            [ModelCapability.VisionCaption, ModelCapability.TextComposition],
            ["en", "ja", "zh-Hans"],
            ["en", "ja", "zh-Hans"],
            ["Hans", "Jpan", "Latn"],
            true,
            [],
            "Apache-2.0",
            "https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct",
            installedBytes,
            installedBytes,
            minRamBytes,
            recommendedHardware,
            "qwen3-vl.image+text.v1",
            "picforlater.analysis.v1",
            [executionProvider]);

    private static async Task EnsureFixtureModelPackagesAsync(
        AppDataPaths paths,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var package in ModelPackages)
        {
            var directoryName = package.PackageKey.Replace('@', '-').Replace('.', '-');
            var installedDirectory = Path.Combine(paths.ModelPackagesDirectoryPath, directoryName);
            Directory.CreateDirectory(installedDirectory);
            var relativePath = Path.GetRelativePath(paths.RootPath, installedDirectory)
                .Replace(Path.DirectorySeparatorChar, '/');
            await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO ModelPackages (
                        PackageKey, PackageId, Version, Backend, Architecture, Quantization,
                        ManifestJson, InstalledRelativePath, BenchmarkStatus,
                        InstalledAtUtc, SelfTestedAtUtc)
                    VALUES (
                        @packageKey, @packageId, @version, @backend, @architecture, @quantization,
                        @manifest, @path, 'SelfTestPassed', @installed, @selfTested)
                    ON CONFLICT(PackageKey) DO UPDATE SET
                        PackageId = excluded.PackageId,
                        Version = excluded.Version,
                        Backend = excluded.Backend,
                        Architecture = excluded.Architecture,
                        Quantization = excluded.Quantization,
                        ManifestJson = excluded.ManifestJson,
                        InstalledRelativePath = excluded.InstalledRelativePath,
                        BenchmarkStatus = excluded.BenchmarkStatus,
                        InstalledAtUtc = excluded.InstalledAtUtc,
                        SelfTestedAtUtc = excluded.SelfTestedAtUtc;
                    """,
                    cancellationToken,
                    ("@packageKey", package.PackageKey),
                    ("@packageId", package.Manifest.Id),
                    ("@version", package.Manifest.Version),
                    ("@backend", package.Manifest.Backend),
                    ("@architecture", package.Manifest.Architecture),
                    ("@quantization", package.Manifest.Quantization),
                    ("@manifest", JsonSerializer.Serialize(package.Manifest, ManifestJsonOptions)),
                    ("@path", relativePath),
                    ("@installed", ToDb(FixtureNowUtc.AddDays(-3))),
                    ("@selfTested", ToDb(FixtureNowUtc.AddDays(-2))))
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateFixtureModelPackagesAsync(
        AppDataPaths paths,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT PackageKey, InstalledRelativePath FROM ModelPackages ORDER BY PackageKey;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var actualKeys = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            actualKeys.Add(reader.GetString(0));
            var installedDirectory = Path.GetFullPath(Path.Combine(
                paths.RootPath,
                reader.GetString(1).Replace('/', Path.DirectorySeparatorChar)));
            if (!Directory.Exists(installedDirectory))
            {
                throw new InvalidDataException("A visual-fixture model package directory is missing.");
            }
        }

        if (!actualKeys.SetEquals(ModelPackages.Select(package => package.PackageKey)))
        {
            throw new InvalidDataException("The dense-v1 model-package fixture failed validation.");
        }
    }

    private static async Task<bool> TryUseExistingFixtureAsync(
        ILibraryService library,
        IReminderService reminders,
        CancellationToken cancellationToken)
    {
        var active = await library.QueryAsync(
                new LibraryQuery(IsDeleted: false, Limit: 100),
                cancellationToken)
            .ConfigureAwait(false);
        var deleted = await library.QueryAsync(
                new LibraryQuery(IsDeleted: true, Limit: 100),
                cancellationToken)
            .ConfigureAwait(false);
        var allEntries = active.Items.Concat(deleted.Items).ToArray();
        if (allEntries.Length == 0)
        {
            var categories = await library.GetCategoriesAsync(cancellationToken).ConfigureAwait(false);
            var candidates = await reminders.GetPendingCandidatesAsync(
                    limit: 1,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var reminderRows = await reminders.GetRemindersAsync(
                    limit: 1,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (categories.Count != 0 || candidates.Count != 0 || reminderRows.Count != 0)
            {
                throw new InvalidDataException(
                    $"The {FixtureId} visual fixture data root contains orphaned fixture state; " +
                    "use a fresh isolated UI-test data root.");
            }

            return false;
        }

        var expectedActiveNames = Items
            .Where(item => !item.IsDeleted)
            .Select(item => item.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedDeletedNames = Items
            .Where(item => item.IsDeleted)
            .Select(item => item.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualActiveNames = active.Items
            .Select(entry => entry.Item.OriginalFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualDeletedNames = deleted.Items
            .Select(entry => entry.Item.OriginalFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isCompleteFixture = active.Items.Count == expectedActiveNames.Count
            && deleted.Items.Count == expectedDeletedNames.Count
            && actualActiveNames.SetEquals(expectedActiveNames)
            && actualDeletedNames.SetEquals(expectedDeletedNames);
        if (!isCompleteFixture)
        {
            throw new InvalidDataException(
                $"The {FixtureId} visual fixture data root is partial or contains unexpected items; " +
                "use a fresh isolated UI-test data root.");
        }

        await ValidateAsync(library, reminders, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<IReadOnlyDictionary<string, Category>> EnsureCategoriesAsync(
        ILibraryService library,
        CancellationToken cancellationToken)
    {
        var categoryNames = Items
            .SelectMany(item => item.CategoryNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existing = (await library.GetCategoriesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(category => category.Name, StringComparer.Ordinal);
        foreach (var categoryName in categoryNames)
        {
            if (!existing.ContainsKey(categoryName))
            {
                existing[categoryName] = await library.CreateCategoryAsync(
                        categoryName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return existing;
    }

    private static async Task WaitForCompletedAnalysisAsync(
        ILibraryService library,
        IEnumerable<Guid> imageItemIds,
        CancellationToken cancellationToken)
    {
        var expectedIds = imageItemIds.ToHashSet();
        await WaitUntilAsync(
                async () =>
                {
                    var active = await library.QueryAsync(
                            new LibraryQuery(IsDeleted: false, Limit: Items.Length),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return active.Items.Count == Items.Length
                        && active.Items.All(entry => expectedIds.Contains(entry.Item.Id))
                        && active.Items.All(entry => entry.Item.AnalysisState == AnalysisState.Completed);
                },
                "The dense-v1 analysis jobs did not reach a stable completed state.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureRemindersAsync(
        IReminderService reminders,
        IReadOnlyDictionary<string, Guid> imageItemIds,
        CancellationToken cancellationToken)
    {
        var definitions = new[]
        {
            new FixtureReminder(
                imageItemIds["visual-dense-01-city-walk.png"],
                "带上相机 · 城市散步",
                new DateTime(2099, 5, 20, 10, 0, 0, DateTimeKind.Unspecified),
                "滨江步道入口",
                ShouldCancel: false),
            new FixtureReminder(
                imageItemIds["visual-dense-05-book-list.png"],
                "归还借阅书籍",
                new DateTime(2099, 7, 12, 16, 30, 0, DateTimeKind.Unspecified),
                "市图书馆",
                ShouldCancel: false),
            new FixtureReminder(
                imageItemIds["visual-dense-09-emoji-board.png"],
                "整理灵感板 ✨",
                new DateTime(2099, 8, 8, 9, 15, 0, DateTimeKind.Unspecified),
                null,
                ShouldCancel: true),
        };

        foreach (var definition in definitions)
        {
            var reminder = (await reminders.GetRemindersAsync(
                    limit: 100,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false))
                .FirstOrDefault(candidate =>
                    candidate.ImageItemId == definition.ImageItemId
                    && candidate.ImageTitle.Equals(definition.Title, StringComparison.Ordinal));
            reminder ??= await reminders.ConfirmAsync(
                    new ReminderConfirmation(
                        definition.ImageItemId,
                        DateCandidateId: null,
                        LocationCandidateId: null,
                        definition.Title,
                        definition.LocalDueDateTime,
                        "UTC",
                        definition.Location),
                    cancellationToken)
                .ConfigureAwait(false);
            if (definition.ShouldCancel && reminder.State == ReminderState.Active)
            {
                await reminders.CancelAsync(reminder.Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task NormalizeRowsAndSeedCandidatesAsync(
        AppDataPaths paths,
        IReadOnlyDictionary<string, Guid> imageItemIds,
        bool includeDeletedAt,
        CancellationToken cancellationToken)
    {
        // Import, editing, category assignment, reminders, and soft deletion all flow
        // through the production services above. Their public contracts deliberately do
        // not expose presentation-state or candidate injection, so this test-only build
        // uses its isolated temporary database solely for fixed timestamps/states and
        // auditable candidate rows; it never changes the schema or production data.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        for (var index = 0; index < Items.Length; index++)
        {
            var item = Items[index];
            var imageItemId = imageItemIds[item.FileName];
            var createdAtUtc = FixtureNowUtc.AddDays(-Items.Length + index);
            var updatedAtUtc = createdAtUtc.AddMinutes(5);
            var deletedAtUtc = includeDeletedAt && item.IsDeleted
                ? FixtureNowUtc.AddMinutes(index)
                : (DateTimeOffset?)null;
            await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE ImageItems
                    SET AnalysisState = @analysisState,
                        CreatedAtUtc = @created,
                        UpdatedAtUtc = @updated,
                        DeletedAtUtc = @deleted
                    WHERE Id = @itemId;
                    """,
                    cancellationToken,
                    ("@analysisState", (int)item.DisplayAnalysisState),
                    ("@created", ToDb(createdAtUtc)),
                    ("@updated", ToDb(updatedAtUtc)),
                    ("@deleted", deletedAtUtc is null ? null : ToDb(deletedAtUtc.Value)),
                    ("@itemId", ToDb(imageItemId)))
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE ImageAssets
                    SET CreatedAtUtc = @created
                    WHERE Id = (SELECT AssetId FROM ImageItems WHERE Id = @itemId);
                    """,
                    cancellationToken,
                    ("@created", ToDb(createdAtUtc)),
                    ("@itemId", ToDb(imageItemId)))
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE ImportJobs
                    SET CreatedAtUtc = @created,
                        UpdatedAtUtc = @updated,
                        CompletedAtUtc = @updated
                    WHERE ImageItemId = @itemId;
                    """,
                    cancellationToken,
                    ("@created", ToDb(createdAtUtc)),
                    ("@updated", ToDb(updatedAtUtc)),
                    ("@itemId", ToDb(imageItemId)))
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE AnalysisJobs
                    SET NotBeforeUtc = @created,
                        LeaseExpiresAtUtc = NULL,
                        CreatedAtUtc = @created,
                        UpdatedAtUtc = @updated,
                        CompletedAtUtc = @updated
                    WHERE ImageItemId = @itemId;
                    """,
                    cancellationToken,
                    ("@created", ToDb(createdAtUtc)),
                    ("@updated", ToDb(updatedAtUtc)),
                    ("@itemId", ToDb(imageItemId)))
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE AnalysisStageResults SET GeneratedAtUtc = @updated WHERE ImageItemId = @itemId;",
                    cancellationToken,
                    ("@updated", ToDb(updatedAtUtc)),
                    ("@itemId", ToDb(imageItemId)))
                .ConfigureAwait(false);
        }

        await ExecuteAsync(
                connection,
                transaction,
                "UPDATE Categories SET CreatedAtUtc = @created, UpdatedAtUtc = @updated;",
                cancellationToken,
                ("@created", ToDb(FixtureNowUtc.AddDays(-30))),
                ("@updated", ToDb(FixtureNowUtc.AddDays(-29))))
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                "UPDATE ImageCategories SET CreatedAtUtc = @created;",
                cancellationToken,
                ("@created", ToDb(FixtureNowUtc.AddDays(-20))))
            .ConfigureAwait(false);

        foreach (var imageItemId in imageItemIds.Values)
        {
            await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM EntityCandidates WHERE ImageItemId = @itemId;",
                    cancellationToken,
                    ("@itemId", ToDb(imageItemId)))
                .ConfigureAwait(false);
        }

        await InsertCandidatePairAsync(
                connection,
                transaction,
                imageItemIds["visual-dense-03-design-review.png"],
                Guid.Parse("d3000000-0000-0000-0000-000000000001"),
                Guid.Parse("d3000000-0000-0000-0000-000000000002"),
                "2099-05-18 09:30",
                "2099-05-18T09:30:00",
                "Room Atlas",
                cancellationToken)
            .ConfigureAwait(false);
        await InsertCandidatePairAsync(
                connection,
                transaction,
                imageItemIds["visual-dense-04-project-retro.png"],
                Guid.Parse("d4000000-0000-0000-0000-000000000001"),
                Guid.Parse("d4000000-0000-0000-0000-000000000002"),
                "2099年6月20日 14:00",
                "2099-06-20T14:00:00",
                "上海市浦东新区世纪大道100号会议室",
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCandidatePairAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid imageItemId,
        Guid dateCandidateId,
        Guid locationCandidateId,
        string rawDate,
        string normalizedDate,
        string location,
        CancellationToken cancellationToken)
    {
        var analysisJobId = await GetAnalysisJobIdAsync(
                connection,
                transaction,
                imageItemId,
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO EntityCandidates (
                    Id, AnalysisJobId, ImageItemId, Kind, RawText,
                    NormalizedValue, Evidence, Source, GeneratedAtUtc,
                    CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                    TimeZoneId, AmbiguityReason, ConfirmedReminderId)
                VALUES (
                    @id, @jobId, @itemId, 'DateTime', @rawText,
                    @normalized, @evidence, 'Ocr', @generated,
                    1, NULL, @reference, 'UTC', NULL, NULL);
                """,
                cancellationToken,
                ("@id", ToDb(dateCandidateId)),
                ("@jobId", ToDb(analysisJobId)),
                ("@itemId", ToDb(imageItemId)),
                ("@rawText", rawDate),
                ("@normalized", normalizedDate),
                ("@evidence", $"OCR: {rawDate}"),
                ("@generated", ToDb(FixtureNowUtc)),
                ("@reference", ToDb(FixtureNowUtc)))
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO EntityCandidates (
                    Id, AnalysisJobId, ImageItemId, Kind, RawText,
                    NormalizedValue, Evidence, Source, GeneratedAtUtc,
                    CandidateStatus, BoundingBoxJson, ReferenceTimeUtc,
                    TimeZoneId, AmbiguityReason, ConfirmedReminderId)
                VALUES (
                    @id, @jobId, @itemId, 'Location', @rawText,
                    @normalized, @evidence, 'Ocr', @generated,
                    1, NULL, @reference, NULL, NULL, NULL);
                """,
                cancellationToken,
                ("@id", ToDb(locationCandidateId)),
                ("@jobId", ToDb(analysisJobId)),
                ("@itemId", ToDb(imageItemId)),
                ("@rawText", location),
                ("@normalized", location),
                ("@evidence", $"OCR: {location}"),
                ("@generated", ToDb(FixtureNowUtc)),
                ("@reference", ToDb(FixtureNowUtc)))
            .ConfigureAwait(false);
    }

    private static async Task<Guid> GetAnalysisJobIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid imageItemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Id FROM AnalysisJobs WHERE ImageItemId = @itemId ORDER BY Kind, CreatedAtUtc LIMIT 1;";
        command.Parameters.AddWithValue("@itemId", ToDb(imageItemId));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string id && Guid.TryParse(id, out var parsed)
            ? parsed
            : throw new InvalidDataException("The visual fixture analysis job is missing.");
    }

    private static async Task WaitForStableReminderOutboxAsync(
        IReminderService reminders,
        CancellationToken cancellationToken)
    {
        await WaitUntilAsync(
                async () =>
                {
                    var rows = await reminders.GetRemindersAsync(
                            limit: 100,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return rows.Count == 3
                        && rows.All(row => row.NotificationState is
                            ReminderNotificationState.Scheduled or ReminderNotificationState.Cancelled);
                },
                "The dense-v1 reminder outbox did not settle.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ValidateAsync(
        ILibraryService library,
        IReminderService reminders,
        CancellationToken cancellationToken)
    {
        var active = await library.QueryAsync(
                new LibraryQuery(IsDeleted: false, Limit: 100),
                cancellationToken)
            .ConfigureAwait(false);
        var deleted = await library.QueryAsync(
                new LibraryQuery(IsDeleted: true, Limit: 100),
                cancellationToken)
            .ConfigureAwait(false);
        var categories = await library.GetCategoriesAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await reminders.GetPendingCandidatesAsync(
                limit: 100,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var reminderRows = await reminders.GetRemindersAsync(
                limit: 100,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var allEntries = active.Items.Concat(deleted.Items).ToArray();
        var expectedCategoryNames = Items
            .SelectMany(item => item.CategoryNames)
            .ToHashSet(StringComparer.Ordinal);
        var actualCategoryNames = categories
            .Select(category => category.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (active.Items.Count != 12
            || deleted.Items.Count != 4
            || allEntries.Select(entry => entry.Item.AnalysisState).Distinct().Count() != 4
            || allEntries.Select(entry => (entry.Asset.PixelWidth, entry.Asset.PixelHeight)).Distinct().Count() < 8
            || categories.Count != expectedCategoryNames.Count
            || !actualCategoryNames.SetEquals(expectedCategoryNames)
            || candidates.Count != 2
            || reminderRows.Count != 3
            || reminderRows.Count(row => row.State == ReminderState.Active) != 2
            || reminderRows.Count(row => row.State == ReminderState.Completed) != 1
            || reminderRows.Any(row => row.NotificationState is not (
                ReminderNotificationState.Scheduled or ReminderNotificationState.Cancelled)))
        {
            throw new InvalidDataException("The dense-v1 visual fixture failed its shape validation.");
        }
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(45))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(timeoutMessage);
    }

    private static MemoryStream CreatePng(int width, int height, int seed)
    {
        var raw = new byte[(checked(width * 4) + 1) * height];
        var rowLength = (width * 4) + 1;
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowLength;
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                var offset = rowStart + 1 + (x * 4);
                var tileX = (x * 8) / width;
                var tileY = (y * 6) / height;
                var accent = (tileX + tileY + seed) % 5;
                raw[offset] = (byte)((seed * 13 + tileX * 29 + accent * 37) & 0xff);
                raw[offset + 1] = (byte)((seed * 7 + tileY * 43 + accent * 19) & 0xff);
                raw[offset + 2] = (byte)((seed * 3 + tileX * 17 + tileY * 31) & 0xff);
                raw[offset + 3] = 0xff;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        output.Position = 0;
        return output;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = UpdateCrc(0xffffffffu, typeBytes);
        crc = UpdateCrc(crc, data) ^ 0xffffffffu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0
                    ? crc >> 1
                    : 0xedb88320u ^ (crc >> 1);
            }
        }

        return crc;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToDb(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record FixtureItem(
        string FileName,
        int PixelWidth,
        int PixelHeight,
        int PatternSeed,
        ImageSourceKind SourceKind,
        string Title,
        string Summary,
        AnalysisState DisplayAnalysisState,
        bool IsDeleted,
        IReadOnlyList<string> CategoryNames);

    private sealed record FixtureReminder(
        Guid ImageItemId,
        string Title,
        DateTime LocalDueDateTime,
        string? Location,
        bool ShouldCancel);

    private sealed record FixtureModelPackage(
        string PackageKey,
        ModelPackageManifest Manifest);

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}

internal sealed class UiTestNvidiaCudaEnvironmentService : INvidiaCudaEnvironmentService
{
    private static readonly NvidiaCudaEnvironmentStatus FixedStatus = new(
        NvidiaCudaEnvironmentState.RuntimeMissing,
        NvidiaCudaRuntimeSource.None,
        new NvidiaGpuDevice(
            "NVIDIA GeForce RTX 4060 Laptop GPU",
            8L * 1024 * 1024 * 1024,
            8,
            9),
        12_800,
        ["cudart64_12.dll", "cublas64_12.dll"]);

    internal UiTestNvidiaCudaEnvironmentService(AppDataPaths paths)
    {
        ManagedRuntimeDirectoryPath = Path.Combine(
            paths.ModelRuntimesDirectoryPath,
            "nvidia-cuda-runtime");
    }

    public NvidiaCudaRuntimePackageInfo RuntimePackage { get; } = new(
        "12.8.2",
        "9.25.0.15",
        2_966_280_000,
        2_350_000_000,
        "https://docs.nvidia.com/cuda/eula/index.html",
        "https://docs.nvidia.com/deeplearning/cudnn/latest/reference/eula.html",
        "https://developer.download.nvidia.com/compute/cuda/redist/");

    public string ManagedRuntimeDirectoryPath { get; }

    public Task<NvidiaCudaEnvironmentStatus> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FixedStatus);
    }

    public Task<NvidiaCudaRuntimeInstallResult> DownloadAndInstallRuntimeAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<NvidiaCudaRuntimeInstallResult>(
            new InvalidOperationException(
                "The deterministic visual fixture never downloads a CUDA runtime."));
    }
}
#endif
