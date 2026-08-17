using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PicForLater.Analysis;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Analysis;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class ModelManagementWorkflowTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    [Fact]
    public async Task ImportAndSlotSwitch_ValidateBeforeCommitAndPreserveOldProfileOnFailure()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var runtime = new FakeQwenRuntime();
        var service = new SqliteModelPackageService(
            root.Paths,
            new QwenModelPackageValidator(
                runtime,
                root.Paths.AnalysisCacheDirectoryPath));
        var manifestPath = await CreatePackageAsync(root.RootPath);

        var imported = await service.ImportAsync(manifestPath);

        Assert.Equal("picforlater.qwen3-vl-2b-instruct-int4@0.1.0", imported.Package.PackageKey);
        Assert.True(Directory.Exists(imported.Package.InstalledDirectoryPath));
        Assert.Equal(1, runtime.CallCount);
        Assert.Equal(InferenceAccelerationMode.Cpu, runtime.LastAccelerationMode);
        Assert.StartsWith(
            root.Paths.AnalysisCacheDirectoryPath + Path.DirectorySeparatorChar,
            runtime.LastImageFilePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(runtime.LastImageFilePath!.Length < 260);
        Assert.False(File.Exists(runtime.LastImageFilePath));
        AssertPngIntegrity(runtime.LastImageBytes, expectedWidth: 32, expectedHeight: 32);
        var before = await service.GetCurrentSnapshotAsync();
        Assert.Null(before.GetSlot(ModelCapability.VisionCaption).PackageKey);

        runtime.Fail = true;
        await Assert.ThrowsAsync<ModelPackageValidationException>(() =>
            service.SwitchAsync(ModelCapability.VisionCaption, imported.Package.PackageKey));
        var failedSwitch = await service.GetCurrentSnapshotAsync();
        Assert.Equal(before.Revision, failedSwitch.Revision);
        Assert.Null(failedSwitch.GetSlot(ModelCapability.VisionCaption).PackageKey);

        runtime.Fail = false;
        await service.SwitchAsync(ModelCapability.VisionCaption, imported.Package.PackageKey);
        var selected = await service.GetCurrentSnapshotAsync();
        Assert.True(selected.Revision > before.Revision);
        Assert.Equal(imported.Package.PackageKey, selected.GetSlot(ModelCapability.VisionCaption).PackageKey);
        Assert.Equal("local.qwen3-vl", selected.GetSlot(ModelCapability.VisionCaption).ProviderId);

        runtime.Fail = true;
        await Assert.ThrowsAsync<ModelPackageValidationException>(() =>
            service.SwitchAsync(ModelCapability.TextComposition, imported.Package.PackageKey));
        var rolledBack = await service.GetCurrentSnapshotAsync();
        Assert.Equal(selected.Revision, rolledBack.Revision);
        Assert.Equal(imported.Package.PackageKey, rolledBack.GetSlot(ModelCapability.VisionCaption).PackageKey);
        Assert.Null(rolledBack.GetSlot(ModelCapability.TextComposition).PackageKey);
        Assert.Equal("local.extractive-text", rolledBack.GetSlot(ModelCapability.TextComposition).ProviderId);
    }

    [Fact]
    public async Task Import_RejectsStructurallyValidButIncorrectSelfTestOutput()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var runtime = new FakeQwenRuntime
        {
            Output =
                """{"schemaVersion":"picforlater.analysis.v1","visualFacts":[],"title":"Unexpected","summary":"","categoryIds":[],"entities":[],"detectedLanguages":["und"],"warnings":[]}""",
        };
        var service = new SqliteModelPackageService(
            root.Paths,
            new QwenModelPackageValidator(
                runtime,
                root.Paths.AnalysisCacheDirectoryPath));
        var manifestPath = await CreatePackageAsync(root.RootPath);

        var exception = await Assert.ThrowsAsync<ModelPackageImportException>(() =>
            service.ImportAsync(manifestPath));

        Assert.Equal("model.import-failed", exception.ErrorCode);
        var validation = Assert.IsType<ModelPackageValidationException>(exception.InnerException);
        Assert.Equal("model.inference-self-test-output-mismatch", validation.ErrorCode);
        Assert.Empty((await service.GetStateAsync()).Packages);
    }

    [Fact]
    public async Task Import_AcceptsCudaOnlyPackageAndRunsCudaSelfTest()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var runtime = new FakeQwenRuntime();
        var service = new SqliteModelPackageService(
            root.Paths,
            new QwenModelPackageValidator(
                runtime,
                root.Paths.AnalysisCacheDirectoryPath));
        var manifestPath = await CreatePackageAsync(root.RootPath, ["CUDA"]);

        var imported = await service.ImportAsync(manifestPath);

        Assert.Equal(["CUDA"], imported.Package.Manifest.SupportedExecutionProviders);
        Assert.Equal(InferenceAccelerationMode.CudaGpu, runtime.LastAccelerationMode);
    }

    [Fact]
    public async Task Import_RejectsPackageWhenItsDeclaredExecutionProviderIsNotPackaged()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var service = new SqliteModelPackageService(
            root.Paths,
            new QwenModelPackageValidator(new FakeQwenRuntime(
                    new HashSet<string>(["CPU"], StringComparer.Ordinal)),
                root.Paths.AnalysisCacheDirectoryPath));
        var manifestPath = await CreatePackageAsync(root.RootPath, ["CUDA"]);

        var exception = await Assert.ThrowsAsync<ModelPackageImportException>(() =>
            service.ImportAsync(manifestPath));

        Assert.Equal("model.import-failed", exception.ErrorCode);
        var validation = Assert.IsType<ModelPackageValidationException>(exception.InnerException);
        Assert.Equal("model.execution-provider-unavailable", validation.ErrorCode);
        Assert.Empty((await service.GetStateAsync()).Packages);
    }

    [Fact]
    public async Task Import_RecoversVerifiedPackageMovedBeforeDatabaseRegistration()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var runtime = new FakeQwenRuntime();
        var service = new SqliteModelPackageService(
            root.Paths,
            new QwenModelPackageValidator(
                runtime,
                root.Paths.AnalysisCacheDirectoryPath));
        var manifestPath = await CreatePackageAsync(root.RootPath);
        var sourceDirectoryPath = Path.GetDirectoryName(manifestPath)!;
        var manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
            await File.ReadAllTextAsync(manifestPath),
            ManifestJsonOptions)!;
        var orphanedInstalledDirectoryPath = Path.Combine(
            root.Paths.ModelPackagesDirectoryPath,
            manifest.Id,
            manifest.Version);
        Directory.CreateDirectory(orphanedInstalledDirectoryPath);
        File.Copy(
            manifestPath,
            Path.Combine(orphanedInstalledDirectoryPath, "manifest.json"));
        foreach (var file in manifest.Files)
        {
            var destinationPath = Path.Combine(orphanedInstalledDirectoryPath, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(Path.Combine(sourceDirectoryPath, file.Path), destinationPath);
        }

        var imported = await service.ImportAsync(manifestPath);

        Assert.Equal($"{manifest.Id}@{manifest.Version}", imported.Package.PackageKey);
        Assert.Equal(orphanedInstalledDirectoryPath, imported.Package.InstalledDirectoryPath);
        Assert.Equal(1, runtime.CallCount);
        var state = await service.GetStateAsync();
        Assert.Single(state.Packages);
        Assert.Equal(imported.Package.PackageKey, state.Packages[0].PackageKey);
    }

    [Fact]
    public async Task Switch_RejectsAChangedManagedPackageIdentityWithoutChangingTheProfile()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var service = new SqliteModelPackageService(
            root.Paths,
            new QwenModelPackageValidator(
                new FakeQwenRuntime(),
                root.Paths.AnalysisCacheDirectoryPath));
        var imported = await service.ImportAsync(await CreatePackageAsync(root.RootPath));
        var before = await service.GetCurrentSnapshotAsync();
        var changedManifest = imported.Package.Manifest with
        {
            Id = "picforlater.qwen3-vl-2b-instruct-int4-changed",
        };
        await File.WriteAllTextAsync(
            Path.Combine(imported.Package.InstalledDirectoryPath, "manifest.json"),
            JsonSerializer.Serialize(changedManifest, ManifestJsonOptions));

        var exception = await Assert.ThrowsAsync<ModelPackageSwitchException>(() =>
            service.SwitchAsync(ModelCapability.VisionCaption, imported.Package.PackageKey));

        Assert.Equal("model.installed-package-changed", exception.ErrorCode);
        var after = await service.GetCurrentSnapshotAsync();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Null(after.GetSlot(ModelCapability.VisionCaption).PackageKey);
    }

    [Fact]
    public async Task SwitchMany_ValidatesOnceAndUpdatesVisionAndTextInOneRevision()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var runtime = new FakeQwenRuntime();
        var service = new SqliteModelPackageService(
            root.Paths,
            new QwenModelPackageValidator(
                runtime,
                root.Paths.AnalysisCacheDirectoryPath));
        var imported = await service.ImportAsync(await CreatePackageAsync(root.RootPath));
        var before = await service.GetCurrentSnapshotAsync();

        await service.SwitchManyAsync(
            [ModelCapability.VisionCaption, ModelCapability.TextComposition],
            imported.Package.PackageKey);

        var after = await service.GetCurrentSnapshotAsync();
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(2, runtime.CallCount);
        Assert.Equal(
            imported.Package.PackageKey,
            after.GetSlot(ModelCapability.VisionCaption).PackageKey);
        Assert.Equal(
            imported.Package.PackageKey,
            after.GetSlot(ModelCapability.TextComposition).PackageKey);
    }

    [Fact]
    public async Task SelectiveReanalysis_PinsCurrentProfileAndIsIdempotentPerItemRevision()
    {
        using var root = new TemporaryAppDataRoot();
        await new SqliteDatabaseInitializer(root.Paths).InitializeAsync();
        var profileService = new SqliteModelPackageService(
            root.Paths,
            new QwenModelPackageValidator(
                new FakeQwenRuntime(),
                root.Paths.AnalysisCacheDirectoryPath));
        await profileService.SetAnalysisModeAsync(AnalysisMode.AlwaysEnhance);
        var storage = new ManagedImageStorage(root.Paths);
        using var importer = new ImageImportService(
            root.Paths,
            storage,
            new FakeImageProcessor(),
            analysisProfileSnapshotProvider: profileService);
        var imported = await importer.ImportAsync(
            new MemoryStream(TinyPng, writable: false),
            "selective.png",
            ImageSourceKind.File,
            ManagedImageFormat.Png);
        await profileService.SetAnalysisModeAsync(AnalysisMode.OcrOnly);
        var expectedSnapshot = await profileService.GetCurrentSnapshotAsync();
        var reanalysis = new SqliteAnalysisReanalysisService(root.Paths, profileService);

        var first = await reanalysis.QueueAsync([imported.ImageItemId, Guid.NewGuid(), imported.ImageItemId]);
        var duplicate = await reanalysis.QueueAsync([imported.ImageItemId]);

        Assert.Equal(2, first.RequestedCount);
        Assert.Equal(1, first.QueuedCount);
        Assert.Equal(1, first.SkippedCount);
        Assert.Equal(0, duplicate.QueuedCount);
        await using var connection = await OpenAsync(root.Paths.DatabasePath);
        Assert.Equal(1L, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM AnalysisJobs WHERE ImageItemId = '{imported.ImageItemId:D}' AND Kind = 2;"));
        Assert.Equal((long)AnalysisMode.OcrOnly, await ScalarAsync(
            connection,
            $"SELECT AnalysisMode FROM AnalysisJobs WHERE ImageItemId = '{imported.ImageItemId:D}' AND Kind = 2;"));
        Assert.Equal(expectedSnapshot.Revision, await ScalarAsync(
            connection,
            $"SELECT ProfileRevision FROM AnalysisJobs WHERE ImageItemId = '{imported.ImageItemId:D}' AND Kind = 2;"));
        Assert.Equal((long)AnalysisMode.OcrOnly, await ScalarAsync(
            connection,
            $"SELECT json_extract(ModelProfileSnapshotJson, '$.analysisMode') FROM AnalysisJobs WHERE ImageItemId = '{imported.ImageItemId:D}' AND Kind = 2;"));
    }

    private static async Task<string> CreatePackageAsync(
        string testRoot,
        IReadOnlyList<string>? supportedExecutionProviders = null)
    {
        var packageDirectory = Path.Combine(testRoot, "local-package-source");
        Directory.CreateDirectory(packageDirectory);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["genai_config.json"] = """{"model":{"type":"qwen3_vl","vision":{"filename":"vision.onnx"},"embedding":{"filename":"embedding.onnx"},"decoder":{"filename":"decoder.onnx"}}}"""u8.ToArray(),
            ["tokenizer.json"] = "{}"u8.ToArray(),
            ["vision.onnx"] = [1, 2, 3],
            ["embedding.onnx"] = [4, 5, 6],
            ["decoder.onnx"] = [7, 8, 9],
        };
        foreach (var (path, bytes) in contents)
        {
            await File.WriteAllBytesAsync(Path.Combine(packageDirectory, path), bytes);
        }

        var files = contents.Select(entry => new ModelPackageFile(
            entry.Key,
            entry.Value.LongLength,
            Convert.ToHexString(SHA256.HashData(entry.Value)).ToLowerInvariant())).ToArray();
        var installedBytes = files.Sum(file => file.ByteLength);
        var manifest = new ModelPackageManifest(
            1,
            "picforlater.qwen3-vl-2b-instruct-int4",
            "0.1.0",
            "onnxruntime-genai",
            "onnx",
            "qwen3-vl-2b-instruct",
            "int4",
            [ModelCapability.VisionCaption, ModelCapability.TextComposition],
            ["und", "zh-Hans", "en"],
            ["zh-Hans", "en"],
            ["Hans", "Latn"],
            true,
            files,
            "Apache-2.0",
            "https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct",
            installedBytes,
            installedBytes,
            4L * 1024 * 1024 * 1024,
            "8 GiB RAM; CPU baseline; compatible GPU optional",
            "qwen3-vl.image+text.v1",
            QwenStructuredOutputParser.SchemaVersion,
            supportedExecutionProviders);
        var manifestPath = Path.Combine(packageDirectory, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJsonOptions));
        return manifestPath;
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
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

    private static void AssertPngIntegrity(
        byte[]? bytes,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.NotNull(bytes);
        ReadOnlySpan<byte> png = bytes;
        ReadOnlySpan<byte> signature =
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(png.Length >= 8);
        Assert.True(png[..8].SequenceEqual(signature));

        var offset = 8;
        var foundHeader = false;
        var foundEnd = false;
        using var compressedImageData = new MemoryStream();
        while (offset < png.Length)
        {
            Assert.True(png.Length - offset >= 12);
            var dataLength = BinaryPrimitives.ReadInt32BigEndian(png.Slice(offset, 4));
            Assert.True(dataLength >= 0);
            var chunkLength = checked(12 + dataLength);
            Assert.True(png.Length - offset >= chunkLength);

            var typeAndData = png.Slice(offset + 4, 4 + dataLength);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                png.Slice(offset + 8 + dataLength, 4));
            Assert.Equal(expectedCrc, ComputePngCrc32(typeAndData));

            var type = typeAndData[..4];
            if (type.SequenceEqual("IHDR"u8))
            {
                Assert.Equal(13, dataLength);
                Assert.Equal(expectedWidth, BinaryPrimitives.ReadInt32BigEndian(typeAndData.Slice(4, 4)));
                Assert.Equal(expectedHeight, BinaryPrimitives.ReadInt32BigEndian(typeAndData.Slice(8, 4)));
                foundHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressedImageData.Write(typeAndData[4..]);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                Assert.Equal(0, dataLength);
                foundEnd = true;
            }

            offset += chunkLength;
        }

        Assert.Equal(png.Length, offset);
        Assert.True(foundHeader);
        Assert.True(foundEnd);
        compressedImageData.Position = 0;
        using var decompressor = new ZLibStream(compressedImageData, CompressionMode.Decompress);
        using var decodedImageData = new MemoryStream();
        decompressor.CopyTo(decodedImageData);
        Assert.Equal(
            expectedHeight * (1 + expectedWidth * 3),
            decodedImageData.Length);
    }

    private static uint ComputePngCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? 0xEDB88320u ^ (crc >> 1)
                    : crc >> 1;
            }
        }

        return ~crc;
    }

    private sealed class FakeQwenRuntime : IQwenGenerationRuntime
    {
        public FakeQwenRuntime(IReadOnlySet<string>? supportedExecutionProviders = null)
        {
            SupportedExecutionProviders = supportedExecutionProviders
                ?? new HashSet<string>(["CPU", "DirectML", "CUDA"], StringComparer.Ordinal);
        }

        public IReadOnlySet<string> SupportedExecutionProviders { get; }

        public int CallCount { get; private set; }

        public bool Fail { get; set; }

        public string Output { get; set; } =
            """{"schemaVersion":"picforlater.analysis.v1","visualFacts":[],"title":"Self test","summary":"","categoryIds":[],"entities":[],"detectedLanguages":["und"],"warnings":[]}""";

        public InferenceAccelerationMode? LastAccelerationMode { get; private set; }

        public string? LastImageFilePath { get; private set; }

        public byte[]? LastImageBytes { get; private set; }

        public Task<string> GenerateAsync(
            string modelDirectoryPath,
            string imageFilePath,
            string prompt,
            string jsonSchema,
            int maximumOutputTokens,
            InferenceAccelerationMode accelerationMode,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastAccelerationMode = accelerationMode;
            LastImageFilePath = imageFilePath;
            LastImageBytes = File.ReadAllBytes(imageFilePath);
            if (Fail)
            {
                throw new InvalidOperationException("Simulated local inference failure.");
            }

            return Task.FromResult(Output);
        }
    }

    private sealed class FakeImageProcessor : IImageContentProcessor
    {
        public Task<ImageInspection> InspectAndCreateThumbnailAsync(
            Stream source,
            CancellationToken cancellationToken = default) => Task.FromResult(new ImageInspection(
                ManagedImageFormat.Png,
                "image/png",
                1,
                1,
                TinyPng));
    }
}
