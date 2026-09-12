using Microsoft.Data.Sqlite;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class LibraryWorkflowTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task Import_PersistsOriginalThumbnailItemAndAnalysisJob()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);

        var result = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "poster.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);

        Assert.Equal(ImageImportStatus.Imported, result.Status);
        var entry = await library.GetAsync(result.ImageItemId);
        Assert.NotNull(entry);
        Assert.Equal("poster", entry.Item.Title);
        Assert.Equal(string.Empty, entry.Item.Notes);
        Assert.Equal(AnalysisState.Pending, entry.Item.AnalysisState);
        Assert.True(File.Exists(root.Paths.Resolve(entry.Asset.OriginalRelativePath)));
        Assert.NotNull(entry.Asset.ThumbnailRelativePath);
        Assert.True(File.Exists(root.Paths.Resolve(entry.Asset.ThumbnailRelativePath!)));

        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ImageItems;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM AnalysisJobs WHERE State = 1;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ImportJobs WHERE State = 3;"));
    }

    [Fact]
    public async Task Import_DuplicateReturnsExistingItemWithoutCreatingAnotherAsset()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);

        var first = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "first.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        await library.UpdateDetailFieldsAsync(
            first.ImageItemId,
            new ImageDetailUpdate(Notes: "保留这份备注"));
        var duplicate = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "second.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);

        Assert.Equal(ImageImportStatus.Duplicate, duplicate.Status);
        Assert.Equal(first.ImageItemId, duplicate.ImageItemId);
        Assert.Equal("保留这份备注", (await library.GetAsync(first.ImageItemId))!.Item.Notes);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ImageItems;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ImageAssets;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ImportJobs WHERE State = 4;"));
    }

    [Fact]
    public async Task Library_UpdateDetailFields_TracksOnlySubmittedContentFields()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "details.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);

        await library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(Notes: "  first line\n😀  "));
        var notesOnly = (await library.GetAsync(imported.ImageItemId))!.Item;
        Assert.Equal("  first line\n😀  ", notesOnly.Notes);
        Assert.Equal("details", notesOnly.Title);
        Assert.Equal(string.Empty, notesOnly.Summary);
        Assert.Equal(ContentFieldSource.Fallback, notesOnly.TitleSource);
        Assert.Equal(ContentFieldSource.Fallback, notesOnly.SummarySource);
        Assert.Equal(0, notesOnly.Revision);

        await library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(Summary: "  a summary  ", Notes: " \t\r\n"));
        var summaryUpdate = (await library.GetAsync(imported.ImageItemId))!.Item;
        Assert.Equal("a summary", summaryUpdate.Summary);
        Assert.Equal(string.Empty, summaryUpdate.Notes);
        Assert.Equal(ContentFieldSource.Fallback, summaryUpdate.TitleSource);
        Assert.Equal(ContentFieldSource.User, summaryUpdate.SummarySource);
        Assert.Equal(1, summaryUpdate.Revision);

        await library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(Title: "  revised title  ", Notes: " note "));
        var titleUpdate = (await library.GetAsync(imported.ImageItemId))!.Item;
        Assert.Equal("revised title", titleUpdate.Title);
        Assert.Equal("a summary", titleUpdate.Summary);
        Assert.Equal(" note ", titleUpdate.Notes);
        Assert.Equal(ContentFieldSource.User, titleUpdate.TitleSource);
        Assert.Equal(ContentFieldSource.User, titleUpdate.SummarySource);
        Assert.Equal(2, titleUpdate.Revision);

        await library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(
                Title: "final title",
                Summary: "final summary",
                Notes: "final notes"));
        var allFields = (await library.GetAsync(imported.ImageItemId))!.Item;
        Assert.Equal("final title", allFields.Title);
        Assert.Equal("final summary", allFields.Summary);
        Assert.Equal("final notes", allFields.Notes);
        Assert.Equal(3, allFields.Revision);
    }

    [Fact]
    public async Task Library_UpdateDetailFields_ValidatesBeforeWritingAndHonorsLegacyNullSummary()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "validation.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);

        await library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(
                Title: "original title",
                Summary: "original summary",
                Notes: "original notes"));

        var maximumNotes = string.Concat(Enumerable.Repeat("😀", 5_000));
        Assert.Equal(10_000, maximumNotes.Length);
        await library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(Notes: maximumNotes));
        Assert.Equal(maximumNotes, (await library.GetAsync(imported.ImageItemId))!.Item.Notes);

        await library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(
                Title: "original title",
                Summary: "original summary",
                Notes: "original notes"));
        var beforeInvalidUpdate = (await library.GetAsync(imported.ImageItemId))!.Item;

        await Assert.ThrowsAsync<ArgumentException>(() => library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(
                Title: "should not be written",
                Notes: new string('n', 10_001))));

        var afterInvalidUpdate = (await library.GetAsync(imported.ImageItemId))!.Item;
        Assert.Equal(beforeInvalidUpdate, afterInvalidUpdate);

        await Assert.ThrowsAsync<ArgumentException>(() => library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(Title: " \t\r\n")));

        await library.UpdateUserFieldsAsync(imported.ImageItemId, "  legacy title  ", null);
        var afterLegacyUpdate = (await library.GetAsync(imported.ImageItemId))!.Item;
        Assert.Equal("legacy title", afterLegacyUpdate.Title);
        Assert.Equal(string.Empty, afterLegacyUpdate.Summary);
        Assert.Equal("original notes", afterLegacyUpdate.Notes);
        Assert.Equal(ContentFieldSource.User, afterLegacyUpdate.TitleSource);
        Assert.Equal(ContentFieldSource.User, afterLegacyUpdate.SummarySource);
        Assert.Equal(beforeInvalidUpdate.Revision + 1, afterLegacyUpdate.Revision);
    }

    [Fact]
    public async Task Library_UpdateDetailFields_LeavesNoOpUntouchedAndRejectsInactiveOrAtomicFailures()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "failure.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var initial = (await library.GetAsync(imported.ImageItemId))!.Item;

        await library.UpdateDetailFieldsAsync(imported.ImageItemId, new ImageDetailUpdate());
        Assert.Equal(initial, (await library.GetAsync(imported.ImageItemId))!.Item);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => library.UpdateDetailFieldsAsync(
            Guid.NewGuid(),
            new ImageDetailUpdate(Notes: "missing")));

        await ExecuteNonQueryAsync(
            root.Paths.DatabasePath,
            """
            CREATE TRIGGER FailImageDetailUpdate
            AFTER UPDATE OF Title, Summary, Notes ON ImageItems
            BEGIN
                SELECT RAISE(ABORT, 'forced detail update failure');
            END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(
                Title: "must not persist",
                Summary: "must not persist",
                Notes: "must not persist")));
        Assert.Equal(initial, (await library.GetAsync(imported.ImageItemId))!.Item);

        await library.SoftDeleteAsync(imported.ImageItemId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(Notes: "deleted")));
        var deleted = (await library.GetAsync(imported.ImageItemId))!.Item;
        Assert.Equal(initial.Title, deleted.Title);
        Assert.Equal(initial.Summary, deleted.Summary);
        Assert.Equal(initial.Notes, deleted.Notes);
    }

    [Fact]
    public async Task BatchStyleImports_AreIndependentWhenOneFileIsDamaged()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var secondPng = TinyPng.Concat(new byte[] { 0x01 }).ToArray();

        await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "first.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        await Assert.ThrowsAsync<ImageImportException>(() => importer.ImportAsync(
            new MemoryStream("not an image"u8.ToArray(), writable: false),
            "broken.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png));
        await importer.ImportAsync(
            new MemoryStream(secondPng, writable: false),
            "second.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);

        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ImageItems;"));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ImportJobs WHERE State = 5;"));
    }

    [Fact]
    public async Task Library_SearchCategoryEditRecycleRestoreAndPermanentDeletePreserveSemantics()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "event.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var category = await library.CreateCategoryAsync("活动");

        await library.SetCategoryAssignmentAsync(imported.ImageItemId, category.Id, isAssigned: true);
        await library.UpdateUserFieldsAsync(imported.ImageItemId, "周末活动", "带上门票");
        await library.UpdateDetailFieldsAsync(
            imported.ImageItemId,
            new ImageDetailUpdate(Notes: "删除恢复后仍保留"));

        var searched = await library.QueryAsync(new LibraryQuery("门票", category.Id));
        var found = Assert.Single(searched.Items);
        Assert.Equal(ContentFieldSource.User, found.Item.TitleSource);
        Assert.Equal("活动", Assert.Single(found.Categories).Category.Name);

        await library.SoftDeleteAsync(imported.ImageItemId);
        Assert.Empty((await library.QueryAsync(new LibraryQuery())).Items);
        var recycled = Assert.Single((await library.QueryAsync(new LibraryQuery(IsDeleted: true))).Items);
        Assert.Equal(category.Id, Assert.Single(recycled.Categories).Category.Id);
        Assert.Equal("删除恢复后仍保留", recycled.Item.Notes);

        await library.RestoreAsync(imported.ImageItemId);
        var restored = Assert.Single((await library.QueryAsync(new LibraryQuery())).Items);
        Assert.Equal("删除恢复后仍保留", restored.Item.Notes);
        await library.SoftDeleteAsync(imported.ImageItemId);
        var originalPath = root.Paths.Resolve(recycled.Asset.OriginalRelativePath);

        var deletion = await library.PermanentlyDeleteAsync(imported.ImageItemId);

        Assert.Equal(PermanentDeleteStatus.Completed, deletion.Status);
        Assert.False(File.Exists(originalPath));
        Assert.Empty((await library.QueryAsync(new LibraryQuery(IsDeleted: true))).Items);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM DeletionJobs WHERE State = 2;"));
    }

    [Fact]
    public async Task RecycleBin_MoreThanTwoPages_CanRestoreAndPermanentlyDeleteFromLaterPage()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);

        for (var index = 0; index < 201; index++)
        {
            var uniquePng = TinyPng.Concat(BitConverter.GetBytes(index)).ToArray();
            var imported = await importer.ImportAsync(
                new MemoryStream(uniquePng, writable: false),
                $"recycled-{index:D3}.png",
                ImageSourceKind.File,
                ManagedImageFormat.Png);
            await library.SoftDeleteAsync(imported.ImageItemId);
        }

        var firstPage = await library.QueryAsync(new LibraryQuery(
            IsDeleted: true,
            Offset: 0,
            Limit: 100));
        var secondPage = await library.QueryAsync(new LibraryQuery(
            IsDeleted: true,
            Offset: firstPage.Items.Count,
            Limit: 100));
        var thirdPage = await library.QueryAsync(new LibraryQuery(
            IsDeleted: true,
            Offset: firstPage.Items.Count + secondPage.Items.Count,
            Limit: 100));

        Assert.Equal(100, firstPage.Items.Count);
        Assert.True(firstPage.HasMore);
        Assert.Equal(100, secondPage.Items.Count);
        Assert.True(secondPage.HasMore);
        Assert.Single(thirdPage.Items);
        Assert.False(thirdPage.HasMore);

        var restoredId = secondPage.Items[0].Item.Id;
        var permanentlyDeletedId = secondPage.Items[1].Item.Id;
        await library.RestoreAsync(restoredId);
        var deletion = await library.PermanentlyDeleteAsync(permanentlyDeletedId);

        Assert.Equal(PermanentDeleteStatus.Completed, deletion.Status);
        Assert.NotNull(await library.GetAsync(restoredId));
        Assert.Null(await library.GetAsync(permanentlyDeletedId));
        Assert.DoesNotContain(
            restoredId,
            (await library.QueryAsync(new LibraryQuery(IsDeleted: true, Limit: 200)))
                .Items
                .Select(entry => entry.Item.Id));
    }

    [Fact]
    public async Task Category_RenameAndDeleteKeepTheImageAndRemoveOnlyAssignments()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "reference.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var category = await library.CreateCategoryAsync("资料");
        await library.SetCategoryAssignmentAsync(imported.ImageItemId, category.Id, isAssigned: true);

        await library.RenameCategoryAsync(category.Id, "参考资料");

        var renamed = Assert.Single(await library.GetCategoriesAsync());
        Assert.Equal("参考资料", renamed.Name);
        Assert.Single((await library.GetAsync(imported.ImageItemId))!.Categories);

        await library.DeleteCategoryAsync(category.Id);

        Assert.Empty(await library.GetCategoriesAsync());
        var entry = await library.GetAsync(imported.ImageItemId);
        Assert.NotNull(entry);
        Assert.Empty(entry.Categories);
        Assert.True(File.Exists(root.Paths.Resolve(entry.Asset.OriginalRelativePath)));
    }

    [Fact]
    public async Task GetSummaries_ReturnsOnlyRequestedExistingImages()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);
        var first = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "first.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var secondBytes = TinyPng.Concat([(byte)0x01]).ToArray();
        var second = await importer.ImportAsync(
            new MemoryStream(secondBytes, writable: false),
            "second.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        await library.UpdateUserFieldsAsync(first.ImageItemId, "First", "first summary");
        await library.UpdateUserFieldsAsync(second.ImageItemId, "Second", "second summary");
        var missingId = Guid.NewGuid();

        var summaries = await library.GetSummariesAsync(
            [first.ImageItemId, second.ImageItemId, first.ImageItemId, missingId]);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("first summary", summaries[first.ImageItemId]);
        Assert.Equal("second summary", summaries[second.ImageItemId]);
        Assert.False(summaries.ContainsKey(missingId));
    }

    [Fact]
    public async Task Library_QuerySortsInSqlByTitleSizeAndCategoryBeforePaging()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(root.Paths, storage, new FakeImageProcessor());
        var library = new LibraryService(root.Paths, storage);

        var small = await importer.ImportAsync(
            new MemoryStream(TinyPng.Concat([(byte)0x01]).ToArray(), writable: false),
            "small.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var large = await importer.ImportAsync(
            new MemoryStream(TinyPng.Concat(Enumerable.Repeat((byte)0x02, 32)).ToArray(), writable: false),
            "large.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        var uncategorized = await importer.ImportAsync(
            new MemoryStream(TinyPng.Concat([(byte)0x03, (byte)0x04]).ToArray(), writable: false),
            "middle.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        await library.UpdateUserFieldsAsync(small.ImageItemId, "Beta", string.Empty);
        await library.UpdateUserFieldsAsync(large.ImageItemId, "Alpha", string.Empty);
        await library.UpdateUserFieldsAsync(uncategorized.ImageItemId, "Gamma", string.Empty);
        var alphaCategory = await library.CreateCategoryAsync("Alpha category");
        var zetaCategory = await library.CreateCategoryAsync("Zeta category");
        await library.SetCategoryAssignmentAsync(small.ImageItemId, zetaCategory.Id, isAssigned: true);
        await library.SetCategoryAssignmentAsync(large.ImageItemId, alphaCategory.Id, isAssigned: true);

        var byTitle = await library.QueryAsync(new LibraryQuery(
            SortField: LibrarySortField.Title,
            SortDirection: LibrarySortDirection.Ascending,
            Limit: 2));
        Assert.Equal(["Alpha", "Beta"], byTitle.Items.Select(entry => entry.Item.Title));
        Assert.True(byTitle.HasMore);

        var bySize = await library.QueryAsync(new LibraryQuery(
            SortField: LibrarySortField.ByteLength,
            SortDirection: LibrarySortDirection.Descending));
        Assert.Equal(
            [large.ImageItemId, uncategorized.ImageItemId, small.ImageItemId],
            bySize.Items.Select(entry => entry.Item.Id));

        var byCategory = await library.QueryAsync(new LibraryQuery(
            SortField: LibrarySortField.Category,
            SortDirection: LibrarySortDirection.Ascending));
        Assert.Equal(
            [large.ImageItemId, small.ImageItemId, uncategorized.ImageItemId],
            byCategory.Items.Select(entry => entry.Item.Id));
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteNonQueryAsync(string databasePath, string sql)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FakeImageProcessor : IImageContentProcessor
    {
        public async Task<ImageInspection> InspectAndCreateThumbnailAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            var header = new byte[8];
            await source.ReadExactlyAsync(header, cancellationToken);
            if (!header.AsSpan().SequenceEqual(TinyPng.AsSpan(0, 8)))
            {
                throw new InvalidDataException("Test image is not PNG.");
            }

            return new ImageInspection(
                ManagedImageFormat.Png,
                "image/png",
                1,
                1,
                TinyPng);
        }
    }
}
