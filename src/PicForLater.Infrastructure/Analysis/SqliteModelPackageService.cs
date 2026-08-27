using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Analysis;

public sealed class SqliteModelPackageService :
    IModelPackageService,
    IRecommendedModelPackageInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
    private readonly AppDataPaths _paths;
    private readonly IModelPackageValidator _validator;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public SqliteModelPackageService(AppDataPaths paths, IModelPackageValidator validator)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<ModelManagementState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        var profile = await GetCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT PackageKey, ManifestJson, InstalledRelativePath,
                   InstalledAtUtc, SelfTestedAtUtc, BenchmarkStatus
            FROM ModelPackages
            ORDER BY PackageId, Version, PackageKey;
            """;
        var packages = new List<InstalledModelPackage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            packages.Add(ReadPackage(reader));
        }

        return new ModelManagementState(profile, packages);
    }

    public async Task<ModelProfileSnapshot> GetCurrentSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        AnalysisMode mode;
        long revision;
        await using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "SELECT AnalysisMode, ProfileRevision FROM AnalysisSettings WHERE Id = 1;";
            await using var reader = await settings.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The analysis settings row is missing.");
            }

            mode = (AnalysisMode)reader.GetInt32(0);
            revision = reader.GetInt64(1);
        }

        var slots = new List<ModelSlotSelection>();
        await using (var profiles = connection.CreateCommand())
        {
            profiles.CommandText =
                "SELECT Capability, ProviderId, PackageKey FROM ModelCapabilityProfiles ORDER BY Capability;";
            await using var reader = await profiles.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                slots.Add(new ModelSlotSelection(
                    (ModelCapability)reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        if (slots.Count != Enum.GetValues<ModelCapability>().Length)
        {
            throw new InvalidDataException("The model capability profile is incomplete.");
        }

        return new ModelProfileSnapshot(mode, revision, slots);
    }

    public async Task<ModelPackageImportResult> ImportAsync(
        string manifestFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFilePath);
        var absoluteManifestPath = Path.GetFullPath(manifestFilePath);
        if (!Path.GetFileName(absoluteManifestPath).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The selected file must be manifest.json.", nameof(manifestFilePath));
        }

        var sourceDirectoryPath = Path.GetDirectoryName(absoluteManifestPath)
            ?? throw new ArgumentException("The selected manifest has no parent directory.", nameof(manifestFilePath));
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectoryPath = null;
        string? installedDirectoryPath = null;
        var movedToInstalledDirectory = false;
        var registrationCommitted = false;
        try
        {
            var sourcePackage = await _validator.ValidateAsync(
                sourceDirectoryPath,
                runInferenceSelfTest: false,
                cancellationToken).ConfigureAwait(false);
            var existing = await ResolveAsync(sourcePackage.PackageKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!PackageFilesEqual(existing.Manifest.Files, sourcePackage.Manifest.Files))
                {
                    throw new ModelPackageImportException("model.same-version-content-conflict");
                }

                return new ModelPackageImportResult(existing, ReplacedExistingPackage: false);
            }

            installedDirectoryPath = Path.Combine(
                _paths.ModelPackagesDirectoryPath,
                sourcePackage.Manifest.Id,
                sourcePackage.Manifest.Version);
            EnsureManagedModelPath(installedDirectoryPath);
            if (Directory.Exists(installedDirectoryPath))
            {
                // A process termination can occur after the atomic directory move
                // but before the SQLite registration commits. Recover that verified
                // package in place instead of copying several GiB again or leaving
                // the user permanently blocked by an install-directory conflict.
                var recoverablePackage = await _validator.ValidateAsync(
                    installedDirectoryPath,
                    runInferenceSelfTest: true,
                    cancellationToken).ConfigureAwait(false);
                if (recoverablePackage.PackageKey != sourcePackage.PackageKey
                    || !PackageFilesEqual(
                        recoverablePackage.Manifest.Files,
                        sourcePackage.Manifest.Files))
                {
                    throw new ModelPackageImportException("model.install-directory-conflict");
                }

                var recoveredAtUtc = DateTimeOffset.UtcNow;
                await RegisterPackageAsync(
                    recoverablePackage,
                    installedDirectoryPath,
                    recoveredAtUtc,
                    cancellationToken).ConfigureAwait(false);
                registrationCommitted = true;
                return new ModelPackageImportResult(
                    new InstalledModelPackage(
                        recoverablePackage.PackageKey,
                        recoverablePackage.Manifest,
                        installedDirectoryPath,
                        recoveredAtUtc,
                        recoverablePackage.SelfTestedAtUtc,
                        "SelfTestPassed"),
                    ReplacedExistingPackage: false);
            }

            var stagingRoot = Path.Combine(_paths.ModelPackagesDirectoryPath, ".staging");
            EnsureManagedModelPath(stagingRoot);
            Directory.CreateDirectory(stagingRoot);
            stagingDirectoryPath = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            EnsureManagedModelPath(stagingDirectoryPath);
            Directory.CreateDirectory(stagingDirectoryPath);
            await CopyPackageAsync(
                sourceDirectoryPath,
                stagingDirectoryPath,
                sourcePackage.Manifest,
                cancellationToken).ConfigureAwait(false);

            var stagedPackage = await _validator.ValidateAsync(
                stagingDirectoryPath,
                runInferenceSelfTest: true,
                cancellationToken).ConfigureAwait(false);
            if (stagedPackage.PackageKey != sourcePackage.PackageKey
                || !PackageFilesEqual(stagedPackage.Manifest.Files, sourcePackage.Manifest.Files))
            {
                throw new ModelPackageImportException("model.staged-package-mismatch");
            }

            if (Directory.Exists(installedDirectoryPath))
            {
                throw new ModelPackageImportException("model.install-directory-conflict");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(installedDirectoryPath)!);
            Directory.Move(stagingDirectoryPath, installedDirectoryPath);
            movedToInstalledDirectory = true;
            stagingDirectoryPath = null;
            var installedAtUtc = DateTimeOffset.UtcNow;
            await RegisterPackageAsync(
                stagedPackage,
                installedDirectoryPath,
                installedAtUtc,
                cancellationToken).ConfigureAwait(false);
            registrationCommitted = true;
            return new ModelPackageImportResult(
                new InstalledModelPackage(
                    stagedPackage.PackageKey,
                    stagedPackage.Manifest,
                    installedDirectoryPath,
                    installedAtUtc,
                    stagedPackage.SelfTestedAtUtc,
                    "SelfTestPassed"),
                ReplacedExistingPackage: false);
        }
        catch (ModelPackageImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ModelPackageImportException("model.import-failed", exception);
        }
        finally
        {
            if (stagingDirectoryPath is not null)
            {
                TryDeleteManagedDirectory(stagingDirectoryPath);
            }

            if (movedToInstalledDirectory && !registrationCommitted && installedDirectoryPath is not null)
            {
                TryDeleteManagedDirectory(installedDirectoryPath);
            }

            _mutationGate.Release();
        }
    }

    async Task<ModelPackageImportResult> IRecommendedModelPackageInstaller.InstallAndSwitchRecommendedAsync(
        string packageDirectoryPath,
        ModelPackageManifest expectedManifest,
        IReadOnlyCollection<ModelCapability> capabilities,
        Action? onReadyToEnable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectoryPath);
        ArgumentNullException.ThrowIfNull(expectedManifest);
        ArgumentNullException.ThrowIfNull(capabilities);
        var selectedCapabilities = capabilities.Distinct().ToArray();
        if (selectedCapabilities.Length == 0
            || selectedCapabilities.Any(capability =>
                !Enum.IsDefined(capability)
                || capability is not ModelCapability.VisionCaption
                    and not ModelCapability.TextComposition)
            || selectedCapabilities.Any(capability =>
                !expectedManifest.Capabilities.Contains(capability)))
        {
            throw new ArgumentException(
                "The recommended package capabilities are invalid.",
                nameof(capabilities));
        }

        var sourceDirectoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(packageDirectoryPath));
        EnsureRecommendedDownloadPath(sourceDirectoryPath);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectoryPath = null;
        string? installedDirectoryPath = null;
        var movedToInstalledDirectory = false;
        var registrationCommitted = false;
        try
        {
            var packageKey = $"{expectedManifest.Id}@{expectedManifest.Version}";
            var existing = await ResolveAsync(packageKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!PackageFilesEqual(existing.Manifest.Files, expectedManifest.Files))
                {
                    throw new ModelPackageImportException("model.same-version-content-conflict");
                }

                var revalidated = await _validator.ValidateAsync(
                    existing.InstalledDirectoryPath,
                    runInferenceSelfTest: true,
                    cancellationToken).ConfigureAwait(false);
                EnsurePackageMatchesExpected(revalidated, expectedManifest);
                onReadyToEnable?.Invoke();
                await SwitchValidatedPackageAsync(
                    selectedCapabilities,
                    existing,
                    packageToRegister: null,
                    cancellationToken).ConfigureAwait(false);
                return new ModelPackageImportResult(existing, ReplacedExistingPackage: false);
            }

            installedDirectoryPath = Path.Combine(
                _paths.ModelPackagesDirectoryPath,
                expectedManifest.Id,
                expectedManifest.Version);
            EnsureManagedModelPath(installedDirectoryPath);
            ValidatedModelPackage validatedPackage;
            if (Directory.Exists(installedDirectoryPath))
            {
                validatedPackage = await _validator.ValidateAsync(
                    installedDirectoryPath,
                    runInferenceSelfTest: true,
                    cancellationToken).ConfigureAwait(false);
                EnsurePackageMatchesExpected(validatedPackage, expectedManifest);
            }
            else
            {
                var stagingRoot = Path.Combine(_paths.ModelPackagesDirectoryPath, ".staging");
                EnsureManagedModelPath(stagingRoot);
                Directory.CreateDirectory(stagingRoot);
                stagingDirectoryPath = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
                EnsureManagedModelPath(stagingDirectoryPath);
                Directory.CreateDirectory(stagingDirectoryPath);
                await CopyVerifiedRecommendedPackageAsync(
                    sourceDirectoryPath,
                    stagingDirectoryPath,
                    expectedManifest,
                    cancellationToken).ConfigureAwait(false);
                validatedPackage = await _validator.ValidateVerifiedStagingAsync(
                    stagingDirectoryPath,
                    expectedManifest,
                    cancellationToken).ConfigureAwait(false);
                EnsurePackageMatchesExpected(validatedPackage, expectedManifest);

                if (Directory.Exists(installedDirectoryPath))
                {
                    throw new ModelPackageImportException("model.install-directory-conflict");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(installedDirectoryPath)!);
                Directory.Move(stagingDirectoryPath, installedDirectoryPath);
                movedToInstalledDirectory = true;
                stagingDirectoryPath = null;
            }

            var installedAtUtc = DateTimeOffset.UtcNow;
            var installedPackage = new InstalledModelPackage(
                validatedPackage.PackageKey,
                validatedPackage.Manifest,
                installedDirectoryPath,
                installedAtUtc,
                validatedPackage.SelfTestedAtUtc,
                "SelfTestPassed");
            onReadyToEnable?.Invoke();
            await SwitchValidatedPackageAsync(
                selectedCapabilities,
                installedPackage,
                validatedPackage,
                cancellationToken).ConfigureAwait(false);
            registrationCommitted = true;
            return new ModelPackageImportResult(
                installedPackage,
                ReplacedExistingPackage: false);
        }
        catch (ModelPackageImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ModelPackageImportException("model.import-failed", exception);
        }
        finally
        {
            if (stagingDirectoryPath is not null)
            {
                TryDeleteManagedDirectory(stagingDirectoryPath);
            }

            if (movedToInstalledDirectory && !registrationCommitted && installedDirectoryPath is not null)
            {
                TryDeleteManagedDirectory(installedDirectoryPath);
            }

            _mutationGate.Release();
        }
    }

    public Task SwitchAsync(
        ModelCapability capability,
        string? packageKey,
        CancellationToken cancellationToken = default) =>
        SwitchManyAsync([capability], packageKey, cancellationToken);

    public async Task SwitchManyAsync(
        IReadOnlyCollection<ModelCapability> capabilities,
        string? packageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var selectedCapabilities = capabilities.Distinct().ToArray();
        if (selectedCapabilities.Length == 0
            || selectedCapabilities.Any(capability => !Enum.IsDefined(capability)))
        {
            throw new ArgumentException("At least one valid capability is required.", nameof(capabilities));
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InstalledModelPackage? package = null;
            if (packageKey is not null)
            {
                if (selectedCapabilities.Any(capability =>
                        capability is not ModelCapability.VisionCaption and not ModelCapability.TextComposition))
                {
                    throw new ModelPackageSwitchException("model.slot-does-not-accept-package");
                }

                package = await ResolveAsync(packageKey, cancellationToken).ConfigureAwait(false)
                    ?? throw new ModelPackageSwitchException("model.package-not-installed");
                if (selectedCapabilities.Any(capability => !package.Manifest.Capabilities.Contains(capability)))
                {
                    throw new ModelPackageSwitchException("model.package-capability-mismatch");
                }

                var revalidated = await _validator.ValidateAsync(
                    package.InstalledDirectoryPath,
                    runInferenceSelfTest: true,
                    cancellationToken).ConfigureAwait(false);
                if (revalidated.PackageKey != package.PackageKey
                    || !PackageFilesEqual(revalidated.Manifest.Files, package.Manifest.Files))
                {
                    throw new ModelPackageSwitchException("model.installed-package-changed");
                }

            }

            var now = DateTimeOffset.UtcNow;
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var revision = await IncrementProfileRevisionAsync(
                connection,
                transaction,
                now,
                cancellationToken).ConfigureAwait(false);
            foreach (var capability in selectedCapabilities)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE ModelCapabilityProfiles
                    SET ProviderId = @providerId, PackageKey = @packageKey,
                        Revision = @revision, UpdatedAtUtc = @updated
                    WHERE Capability = @capability;
                    """,
                    cancellationToken,
                    ("@providerId", package is null ? GetBuiltInProviderId(capability) : "local.qwen3-vl"),
                    ("@packageKey", packageKey),
                    ("@revision", revision),
                    ("@updated", ToDb(now)),
                    ("@capability", (int)capability)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SetAnalysisModeAsync(
        AnalysisMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await GetCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (current.AnalysisMode == mode)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE AnalysisSettings
                SET AnalysisMode = @mode, ProfileRevision = ProfileRevision + 1, UpdatedAtUtc = @updated
                WHERE Id = 1;
                """,
                cancellationToken,
                ("@mode", (int)mode),
                ("@updated", ToDb(now))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<InstalledModelPackage?> ResolveAsync(
        string packageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageKey);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT PackageKey, ManifestJson, InstalledRelativePath,
                   InstalledAtUtc, SelfTestedAtUtc, BenchmarkStatus
            FROM ModelPackages WHERE PackageKey = @packageKey;
            """;
        command.Parameters.AddWithValue("@packageKey", packageKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPackage(reader) : null;
    }

    private async Task RegisterPackageAsync(
        ValidatedModelPackage package,
        string installedDirectoryPath,
        DateTimeOffset installedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await InsertPackageAsync(
            connection,
            transaction,
            package,
            installedDirectoryPath,
            installedAtUtc,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SwitchValidatedPackageAsync(
        IReadOnlyCollection<ModelCapability> capabilities,
        InstalledModelPackage installedPackage,
        ValidatedModelPackage? packageToRegister,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (packageToRegister is not null)
        {
            await InsertPackageAsync(
                connection,
                transaction,
                packageToRegister,
                installedPackage.InstalledDirectoryPath,
                installedPackage.InstalledAtUtc,
                cancellationToken).ConfigureAwait(false);
        }

        var revision = await IncrementProfileRevisionAsync(
            connection,
            transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        foreach (var capability in capabilities)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE ModelCapabilityProfiles
                SET ProviderId = 'local.qwen3-vl', PackageKey = @packageKey,
                    Revision = @revision, UpdatedAtUtc = @updated
                WHERE Capability = @capability;
                """,
                cancellationToken,
                ("@packageKey", installedPackage.PackageKey),
                ("@revision", revision),
                ("@updated", ToDb(now)),
                ("@capability", (int)capability)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertPackageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ValidatedModelPackage package,
        string installedDirectoryPath,
        DateTimeOffset installedAtUtc,
        CancellationToken cancellationToken)
    {
        var relativePath = Path.GetRelativePath(_paths.RootPath, installedDirectoryPath)
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
                @manifest, @path, 'SelfTestPassed', @installed, @selfTested);
            """,
            cancellationToken,
            ("@packageKey", package.PackageKey),
            ("@packageId", package.Manifest.Id),
            ("@version", package.Manifest.Version),
            ("@backend", package.Manifest.Backend),
            ("@architecture", package.Manifest.Architecture),
            ("@quantization", package.Manifest.Quantization),
            ("@manifest", package.ManifestJson),
            ("@path", relativePath),
            ("@installed", ToDb(installedAtUtc)),
            ("@selfTested", ToDb(package.SelfTestedAtUtc))).ConfigureAwait(false);
    }

    private InstalledModelPackage ReadPackage(SqliteDataReader reader)
    {
        var manifest = JsonSerializer.Deserialize<ModelPackageManifest>(reader.GetString(1), JsonOptions)
            ?? throw new InvalidDataException("An installed model manifest could not be read.");
        var absolutePath = Path.GetFullPath(Path.Combine(
            _paths.RootPath,
            reader.GetString(2).Replace('/', Path.DirectorySeparatorChar)));
        EnsureManagedModelPath(absolutePath);
        return new InstalledModelPackage(
            reader.GetString(0),
            manifest,
            absolutePath,
            ParseDate(reader.GetString(3)),
            ParseDate(reader.GetString(4)),
            reader.GetString(5));
    }

    private static async Task<long> IncrementProfileRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE AnalysisSettings SET ProfileRevision = ProfileRevision + 1, UpdatedAtUtc = @updated WHERE Id = 1;",
            cancellationToken,
            ("@updated", ToDb(now))).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ProfileRevision FROM AnalysisSettings WHERE Id = 1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static string GetBuiltInProviderId(ModelCapability capability) => capability switch
    {
        ModelCapability.Ocr => "local.fallback-ocr",
        ModelCapability.VisionCaption => "local.none",
        ModelCapability.TextComposition => "local.extractive-text",
        ModelCapability.EntityExtraction => "local.deterministic-entities",
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };

    private static bool PackageFilesEqual(
        IReadOnlyList<ModelPackageFile> left,
        IReadOnlyList<ModelPackageFile> right) =>
        left.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                right.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase),
                ModelPackageFileComparer.Instance);

    private static void EnsurePackageMatchesExpected(
        ValidatedModelPackage package,
        ModelPackageManifest expectedManifest)
    {
        var expectedPackageKey = $"{expectedManifest.Id}@{expectedManifest.Version}";
        if (!package.PackageKey.Equals(expectedPackageKey, StringComparison.Ordinal)
            || !CanonicalManifestJson(package.Manifest).Equals(
                CanonicalManifestJson(expectedManifest),
                StringComparison.Ordinal))
        {
            throw new ModelPackageImportException("model.staged-package-mismatch");
        }
    }

    private static string CanonicalManifestJson(ModelPackageManifest manifest) =>
        JsonSerializer.Serialize(manifest, JsonOptions);

    private async Task CopyVerifiedRecommendedPackageAsync(
        string sourceDirectoryPath,
        string destinationDirectoryPath,
        ModelPackageManifest expectedManifest,
        CancellationToken cancellationToken)
    {
        var manifestJson = CanonicalManifestJson(expectedManifest);
        await File.WriteAllTextAsync(
            Path.Combine(destinationDirectoryPath, "manifest.json"),
            manifestJson,
            cancellationToken).ConfigureAwait(false);
        var totalBytes = 0L;
        foreach (var file in expectedManifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ResolveContainedPackagePath(sourceDirectoryPath, file.Path);
            var destinationPath = ResolveContainedPackagePath(destinationDirectoryPath, file.Path);
            _paths.EnsureSafePath(sourcePath);
            EnsureManagedModelPath(destinationPath);
            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists
                || sourceInfo.Length != file.ByteLength
                || (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ModelPackageImportException("model.staged-package-mismatch");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await CopyAndVerifyFileAsync(
                sourcePath,
                destinationPath,
                file,
                cancellationToken).ConfigureAwait(false);
            totalBytes = checked(totalBytes + file.ByteLength);
        }

        if (totalBytes != expectedManifest.InstalledBytes)
        {
            throw new ModelPackageImportException("model.staged-package-mismatch");
        }
    }

    private static async Task CopyAndVerifyFileAsync(
        string sourcePath,
        string destinationPath,
        ModelPackageFile expectedFile,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        var copiedBytes = 0L;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                copiedBytes = checked(copiedBytes + read);
                if (copiedBytes > expectedFile.ByteLength)
                {
                    throw new ModelPackageImportException("model.staged-package-mismatch");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (copiedBytes != expectedFile.ByteLength
            || !actualHash.Equals(expectedFile.Sha256, StringComparison.Ordinal))
        {
            throw new ModelPackageImportException("model.staged-package-mismatch");
        }
    }

    private static string ResolveContainedPackagePath(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal)
            || relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new ModelPackageImportException("model.staged-package-mismatch");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelPackageImportException("model.staged-package-mismatch");
        }

        return candidate;
    }

    private static async Task CopyPackageAsync(
        string sourceDirectoryPath,
        string destinationDirectoryPath,
        ModelPackageManifest manifest,
        CancellationToken cancellationToken)
    {
        await CopyFileAsync(
            Path.Combine(sourceDirectoryPath, "manifest.json"),
            Path.Combine(destinationDirectoryPath, "manifest.json"),
            cancellationToken).ConfigureAwait(false);
        foreach (var file in manifest.Files)
        {
            var relativePath = file.Path.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.Combine(destinationDirectoryPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await CopyFileAsync(
                Path.Combine(sourceDirectoryPath, relativePath),
                destinationPath,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            131_072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureManagedModelPath(string path)
    {
        _paths.EnsureSafePath(path);
        var modelRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.ModelPackagesDirectoryPath));
        var candidate = Path.GetFullPath(path);
        if (!candidate.Equals(modelRoot, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(modelRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The model path is outside the managed model directory.");
        }
    }

    private void EnsureRecommendedDownloadPath(string path)
    {
        _paths.EnsureSafePath(path);
        var downloadRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(_paths.ModelDownloadStagingDirectoryPath));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!candidate.StartsWith(
                downloadRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The recommended package source is outside the managed download staging directory.");
        }
    }

    private void TryDeleteManagedDirectory(string path)
    {
        try
        {
            EnsureManagedModelPath(path);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // A failed compensation is intentionally not allowed to replace the
            // original import error. Startup cleanup can retry the staging path.
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _paths.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
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

    private static string ToDb(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class ModelPackageFileComparer : IEqualityComparer<ModelPackageFile>
    {
        public static ModelPackageFileComparer Instance { get; } = new();

        public bool Equals(ModelPackageFile? x, ModelPackageFile? y) =>
            x is not null && y is not null
            && x.Path.Equals(y.Path, StringComparison.OrdinalIgnoreCase)
            && x.ByteLength == y.ByteLength
            && x.Sha256.Equals(y.Sha256, StringComparison.Ordinal);

        public int GetHashCode(ModelPackageFile obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path),
            obj.ByteLength,
            StringComparer.Ordinal.GetHashCode(obj.Sha256));
    }
}

public sealed class ModelPackageImportException : Exception, IModelOperationFailure
{
    public ModelPackageImportException(string errorCode, Exception? innerException = null)
        : base("The local model package could not be imported.", innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}

public sealed class ModelPackageSwitchException : Exception, IModelOperationFailure
{
    public ModelPackageSwitchException(string errorCode)
        : base("The local model package could not be selected.") => ErrorCode = errorCode;

    public string ErrorCode { get; }
}
