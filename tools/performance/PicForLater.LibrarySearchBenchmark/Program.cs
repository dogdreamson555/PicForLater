using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.Storage;

const int iterationCount = 30;
const int warmupCount = 3;
int[] fixtureSizes = [1_000, 10_000];

Console.WriteLine($"Runtime: {Environment.Version}; OS: {Environment.OSVersion}");
Console.WriteLine($"Iterations: {iterationCount}; warmups: {warmupCount}");
Console.WriteLine();
Console.WriteLine("| Items | Workload | p50 (ms) | p95 (ms) | Rows |");
Console.WriteLine("|---:|---|---:|---:|---:|");

foreach (var fixtureSize in fixtureSizes)
{
    var rootPath = CreateBenchmarkRoot(fixtureSize);
    try
    {
        var paths = new AppDataPaths(rootPath);
        await new SqliteDatabaseInitializer(paths).InitializeAsync();
        var categoryIds = await SeedAsync(paths.DatabasePath, fixtureSize);
        var library = new LibraryService(paths, new ManagedImageStorage(paths));
        var workloads = CreateWorkloads(fixtureSize, categoryIds[0]);

        foreach (var workload in workloads)
        {
            var result = await MeasureAsync(library, workload.Query);
            Console.WriteLine(
                $"| {fixtureSize:N0} | {workload.Name} | {result.P50Milliseconds:F2} | " +
                $"{result.P95Milliseconds:F2} | {result.ResultCount} |");
        }

        Console.WriteLine();
        Console.WriteLine($"Query plans for {fixtureSize:N0} items:");
        foreach (var workload in workloads)
        {
            Console.WriteLine($"- {workload.Name}");
            foreach (var detail in await ExplainAsync(paths.DatabasePath, workload.Query))
            {
                Console.WriteLine($"  - {detail}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Database size for {fixtureSize:N0} items: " +
            $"{new FileInfo(paths.DatabasePath).Length / (1024d * 1024d):F2} MiB");
        Console.WriteLine();
    }
    finally
    {
        DeleteBenchmarkRoot(rootPath);
    }
}

static string CreateBenchmarkRoot(int fixtureSize)
{
    var root = Path.Combine(
        Path.GetTempPath(),
        $"PicForLater-LibrarySearch-{fixtureSize}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return Path.GetFullPath(root);
}

static void DeleteBenchmarkRoot(string rootPath)
{
    var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    if (!candidate.StartsWith(
            temporaryRoot + Path.DirectorySeparatorChar + "PicForLater-LibrarySearch-",
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Refusing to delete a directory outside benchmark staging.");
    }

    if (Directory.Exists(candidate))
    {
        Directory.Delete(candidate, recursive: true);
    }
}

static IReadOnlyList<BenchmarkWorkload> CreateWorkloads(int fixtureSize, Guid categoryId) =>
[
    new(
        "search-sparse-match",
        new LibraryQuery(SearchText: "needle", Limit: 100)),
    new(
        "search-no-match",
        new LibraryQuery(SearchText: "absent-token", Limit: 100)),
    new(
        "created-deep-page",
        new LibraryQuery(Offset: Math.Max(0, fixtureSize - 100), Limit: 100)),
    new(
        "category-filter",
        new LibraryQuery(CategoryId: categoryId, Limit: 100)),
    new(
        "category-sort",
        new LibraryQuery(
            SortField: LibrarySortField.Category,
            SortDirection: LibrarySortDirection.Ascending,
            Limit: 100)),
];

static async Task<BenchmarkResult> MeasureAsync(
    LibraryService library,
    LibraryQuery query)
{
    LibraryQueryResult? result = null;
    for (var index = 0; index < warmupCount; index++)
    {
        result = await library.QueryAsync(query);
    }

    var samples = new double[iterationCount];
    for (var index = 0; index < samples.Length; index++)
    {
        var started = Stopwatch.GetTimestamp();
        result = await library.QueryAsync(query);
        samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    Array.Sort(samples);
    return new BenchmarkResult(
        Percentile(samples, 0.50),
        Percentile(samples, 0.95),
        result?.Items.Count ?? 0);
}

static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
{
    var index = Math.Clamp(
        (int)Math.Ceiling(percentile * sortedSamples.Count) - 1,
        0,
        sortedSamples.Count - 1);
    return sortedSamples[index];
}

static async Task<Guid[]> SeedAsync(string databasePath, int fixtureSize)
{
    await using var connection = await OpenAsync(databasePath);
    await using var transaction = connection.BeginTransaction(deferred: false);
    var categoryIds = Enumerable.Range(1, 20)
        .Select(index => DeterministicGuid(4, index))
        .ToArray();

    await using (var category = connection.CreateCommand())
    {
        category.Transaction = transaction;
        category.CommandText =
            "INSERT INTO Categories (Id, Name, CreatedAtUtc, UpdatedAtUtc) " +
            "VALUES (@id, @name, @created, @created);";
        category.Parameters.Add("@id", SqliteType.Text);
        category.Parameters.Add("@name", SqliteType.Text);
        category.Parameters.Add("@created", SqliteType.Text);
        for (var index = 0; index < categoryIds.Length; index++)
        {
            category.Parameters["@id"].Value = categoryIds[index].ToString("D");
            category.Parameters["@name"].Value = $"Category {index + 1:D2}";
            category.Parameters["@created"].Value = "2025-01-01T00:00:00.0000000+00:00";
            await category.ExecuteNonQueryAsync();
        }
    }

    await using var insert = connection.CreateCommand();
    insert.Transaction = transaction;
    insert.CommandText =
        """
        INSERT INTO ImageAssets (
            Id, ContentHash, OriginalRelativePath, ThumbnailRelativePath,
            MediaType, ByteLength, PixelWidth, PixelHeight, CreatedAtUtc)
        VALUES (
            @assetId, @hash, @path, NULL,
            'image/png', @bytes, 1920, 1080, @created);

        INSERT INTO ImageItems (
            Id, AssetId, OriginalFileName, SourceKind, Title, Summary,
            TitleSource, SummarySource, AnalysisState, Revision,
            CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
        VALUES (
            @itemId, @assetId, @fileName, 1, @title, @summary,
            2, 2, 3, 1,
            @created, @created, NULL);

        INSERT INTO ImageCategories (ImageItemId, CategoryId, Source, CreatedAtUtc)
        VALUES (@itemId, @categoryId, 2, @created);

        INSERT INTO AnalysisStageResults (
            Id, AnalysisJobId, ImageItemId, Stage, InputRevision,
            ProviderId, ModelId, ModelVersion, ModelFileHashesJson,
            LanguageTagsJson, SchemaVersion, PayloadJson, FactText,
            WarningsJson, GeneratedAtUtc)
        VALUES (
            @analysisId, NULL, @itemId, 1, 1,
            'benchmark.ocr', NULL, NULL, '{}',
            '["en"]', 'benchmark.v1', '{}', @factText,
            '[]', @created);

        INSERT INTO Reminders (
            Id, ImageItemId, DueAtUtc, TimeZoneId, ConfirmedLocation,
            SchedulerId, State, CreatedAtUtc, UpdatedAtUtc)
        SELECT
            @reminderId, @itemId, @due, 'UTC', @location,
            @schedulerId, 1, @created, @created
        WHERE @hasReminder = 1;
        """;
    foreach (var parameter in new[]
             {
                 "@assetId", "@hash", "@path", "@bytes", "@created", "@itemId",
                 "@fileName", "@title", "@summary", "@categoryId", "@analysisId",
                 "@factText", "@reminderId", "@due", "@location", "@schedulerId",
                 "@hasReminder",
             })
    {
        insert.Parameters.Add(new SqliteParameter(parameter, DBNull.Value));
    }

    var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    for (var index = 1; index <= fixtureSize; index++)
    {
        var created = baseTime.AddSeconds(index).ToString("O", CultureInfo.InvariantCulture);
        insert.Parameters["@assetId"].Value = DeterministicGuid(1, index).ToString("D");
        insert.Parameters["@hash"].Value = index.ToString("x64", CultureInfo.InvariantCulture);
        insert.Parameters["@path"].Value = $"assets/originals/{index:D8}.png";
        insert.Parameters["@bytes"].Value = 100_000 + index;
        insert.Parameters["@created"].Value = created;
        insert.Parameters["@itemId"].Value = DeterministicGuid(2, index).ToString("D");
        insert.Parameters["@fileName"].Value = $"photo-{index:D8}.png";
        insert.Parameters["@title"].Value = index % 100 == 0
            ? $"Needle receipt {index:D8}"
            : $"Photo {index:D8}";
        insert.Parameters["@summary"].Value =
            $"Ordinary benchmark summary {index:D8} " + new string('s', 96);
        insert.Parameters["@categoryId"].Value = categoryIds[(index - 1) % categoryIds.Length].ToString("D");
        insert.Parameters["@analysisId"].Value = DeterministicGuid(3, index).ToString("D");
        insert.Parameters["@factText"].Value = (index % 250 == 0
            ? $"OCR needle text {index:D8} "
            : $"Ordinary OCR text {index:D8} ") + new string('资', 512);
        insert.Parameters["@reminderId"].Value = DeterministicGuid(5, index).ToString("D");
        insert.Parameters["@due"].Value = baseTime.AddDays(index % 365).ToString("O", CultureInfo.InvariantCulture);
        insert.Parameters["@location"].Value = index % 500 == 0 ? "Needle Hall" : "Office";
        insert.Parameters["@schedulerId"].Value = $"benchmark-{index:D8}";
        insert.Parameters["@hasReminder"].Value = index % 20 == 0 ? 1 : 0;
        await insert.ExecuteNonQueryAsync();
    }

    await transaction.CommitAsync();
    return categoryIds;
}

static Guid DeterministicGuid(int kind, int index) =>
    Guid.Parse($"00000000-0000-0000-{kind:x4}-{index:x12}");

static async Task<IReadOnlyList<string>> ExplainAsync(
    string databasePath,
    LibraryQuery query)
{
    await using var connection = await OpenAsync(databasePath);
    await using var command = connection.CreateCommand();
    command.CommandText = "EXPLAIN QUERY PLAN " + CreateQuerySql(query);
    var search = query.SearchText?.Trim() ?? string.Empty;
    command.Parameters.AddWithValue(
        "@categoryId",
        query.CategoryId?.ToString("D", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
    command.Parameters.AddWithValue("@search", search);
    command.Parameters.AddWithValue("@pattern", $"%{search}%");
    command.Parameters.AddWithValue("@limit", query.Limit + 1);
    command.Parameters.AddWithValue("@offset", query.Offset);

    var details = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        details.Add(reader.GetString(3));
    }

    return details;
}

static string CreateQuerySql(LibraryQuery query)
{
    // This diagnostic copy is used only for EXPLAIN output; timings above always call the
    // production LibraryService. Keep it aligned with SqliteLibraryStore.QueryAsync.
    var direction = query.SortDirection == LibrarySortDirection.Ascending ? "ASC" : "DESC";
    var orderBy = query.SortField switch
    {
        LibrarySortField.CreatedAt => $"i.CreatedAtUtc {direction}, i.Id ASC",
        LibrarySortField.Title =>
            $"i.Title COLLATE NOCASE {direction}, i.CreatedAtUtc DESC, i.Id ASC",
        LibrarySortField.ByteLength =>
            $"a.ByteLength {direction}, i.CreatedAtUtc DESC, i.Id ASC",
        LibrarySortField.Category =>
            $"""
            CASE WHEN EXISTS (
                SELECT 1 FROM ImageCategories oic WHERE oic.ImageItemId = i.Id
            ) THEN 0 ELSE 1 END ASC,
            (SELECT MIN(oc.Name) FROM ImageCategories oic
             INNER JOIN Categories oc ON oc.Id = oic.CategoryId
             WHERE oic.ImageItemId = i.Id) COLLATE NOCASE {direction},
            i.CreatedAtUtc DESC, i.Id ASC
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(query)),
    };

    return
        $"""
        SELECT
            i.Id, i.AssetId, i.OriginalFileName, i.SourceKind, i.Title, i.Summary,
            i.TitleSource, i.SummarySource, i.AnalysisState, i.Revision,
            i.CreatedAtUtc, i.UpdatedAtUtc, i.DeletedAtUtc,
            a.Id, a.ContentHash, a.OriginalRelativePath, a.ThumbnailRelativePath,
            a.MediaType, a.ByteLength, a.PixelWidth, a.PixelHeight, a.CreatedAtUtc
        FROM ImageItems i
        INNER JOIN ImageAssets a ON a.Id = i.AssetId
        WHERE i.DeletedAtUtc IS NULL
          AND (@categoryId IS NULL OR EXISTS (
                SELECT 1 FROM ImageCategories ic
                WHERE ic.ImageItemId = i.Id AND ic.CategoryId = @categoryId))
          AND (@search = ''
               OR i.Title LIKE @pattern ESCAPE '\' COLLATE NOCASE
               OR i.Summary LIKE @pattern ESCAPE '\' COLLATE NOCASE
               OR EXISTS (
                    SELECT 1 FROM AnalysisStageResults ar
                    WHERE ar.ImageItemId = i.Id
                      AND ar.Stage = 1
                      AND ar.FactText LIKE @pattern ESCAPE '\' COLLATE NOCASE)
               OR EXISTS (
                    SELECT 1 FROM ImageCategories sic
                    INNER JOIN Categories sc ON sc.Id = sic.CategoryId
                    WHERE sic.ImageItemId = i.Id
                      AND sc.Name LIKE @pattern ESCAPE '\' COLLATE NOCASE)
               OR EXISTS (
                    SELECT 1 FROM Reminders r
                    WHERE r.ImageItemId = i.Id
                      AND r.ConfirmedLocation LIKE @pattern ESCAPE '\' COLLATE NOCASE))
        ORDER BY {orderBy}
        LIMIT @limit OFFSET @offset;
        """;
}

static async Task<SqliteConnection> OpenAsync(string databasePath)
{
    var connection = new SqliteConnection(
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
    await command.ExecuteNonQueryAsync();
    return connection;
}

internal sealed record BenchmarkWorkload(string Name, LibraryQuery Query);

internal sealed record BenchmarkResult(
    double P50Milliseconds,
    double P95Milliseconds,
    int ResultCount);
