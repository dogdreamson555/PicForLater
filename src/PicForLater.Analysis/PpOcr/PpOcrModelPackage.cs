using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PicForLater.Analysis.PpOcr;

public sealed class PpOcrModelPackageManifest
{
    public int ManifestVersion { get; init; }

    public string Id { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Backend { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    public string Architecture { get; init; } = string.Empty;

    public string[] Capabilities { get; init; } = [];

    public string[] InputLanguages { get; init; } = [];

    public string[] OutputLanguages { get; init; } = [];

    public string[] Scripts { get; init; } = [];

    public bool MixedLanguageSupport { get; init; }

    public PpOcrModelPackageFile[] Files { get; init; } = [];

    public string License { get; init; } = string.Empty;

    public string SourceUrl { get; init; } = string.Empty;

    public long DownloadBytes { get; init; }

    public long InstalledBytes { get; init; }

    public long MinRamBytes { get; init; }

    public string RecommendedHardware { get; init; } = string.Empty;

    public PpOcrInputSignature InputSignature { get; init; } = new();

    public string OutputSchemaVersion { get; init; } = string.Empty;
}

public sealed class PpOcrModelPackageFile
{
    public string Role { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public long Bytes { get; init; }
}

public sealed class PpOcrInputSignature
{
    public PpOcrTensorSignature Detection { get; init; } = new();

    public PpOcrTensorSignature Recognition { get; init; } = new();

    public int DetectionMaxSideLength { get; init; } = 960;

    public float DetectionThreshold { get; init; } = 0.3f;

    public float BoxThreshold { get; init; } = 0.5f;

    public int RecognitionHeight { get; init; } = 48;

    public int RecognitionWidth { get; init; } = 320;

    public int CtcBlankIndex { get; init; }

    public bool AppendSpaceCharacter { get; init; } = true;
}

public sealed class PpOcrTensorSignature
{
    public string InputName { get; init; } = string.Empty;

    public string OutputName { get; init; } = string.Empty;
}

public sealed record ValidatedPpOcrModelPackage(
    PpOcrModelPackageManifest Manifest,
    string DetectionModelPath,
    string RecognitionModelPath,
    IReadOnlyList<string> Dictionary,
    IReadOnlyDictionary<string, string> FileHashes);

public static class PpOcrModelPackageValidator
{
    public const int SupportedManifestVersion = 1;
    public const long MaximumInstalledBytes = 120L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<ValidatedPpOcrModelPackage> ValidateAsync(
        string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
        RejectReparsePoint(root);
        var manifestPath = ResolvePackagePath(root, "manifest.json");
        await using var manifestStream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (manifestStream.Length is <= 0 or > 256 * 1024)
        {
            throw new InvalidDataException("The model manifest size is invalid.");
        }

        var manifest = await JsonSerializer.DeserializeAsync<PpOcrModelPackageManifest>(
            manifestStream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The model manifest is empty.");
        ValidateManifest(manifest);

        var pathsByRole = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashesByRole = new Dictionary<string, string>(StringComparer.Ordinal);
        long measuredBytes = 0;
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePackagePath(root, file.Path);
            RejectReparsePoint(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Bytes || file.Bytes <= 0)
            {
                throw new InvalidDataException("A model package file has an invalid size.");
            }

            measuredBytes = checked(measuredBytes + info.Length);
            if (measuredBytes > MaximumInstalledBytes)
            {
                throw new InvalidDataException("The OCR model package exceeds the installed-size limit.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!actualHash.Equals(file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A model package file failed SHA-256 validation.");
            }

            if (!pathsByRole.TryAdd(file.Role, path)
                || !hashesByRole.TryAdd(file.Role, actualHash))
            {
                throw new InvalidDataException("A model package file role is duplicated.");
            }
        }

        if (measuredBytes != manifest.InstalledBytes)
        {
            throw new InvalidDataException("The model package installed size does not match its manifest.");
        }

        var dictionary = await ReadDictionaryAsync(
            pathsByRole["dictionary"],
            cancellationToken).ConfigureAwait(false);
        if (manifest.InputSignature.AppendSpaceCharacter
            && !dictionary.Contains(" ", StringComparer.Ordinal))
        {
            dictionary.Add(" ");
        }

        if (dictionary.Count == 0)
        {
            throw new InvalidDataException("The OCR recognition dictionary is empty.");
        }

        return new ValidatedPpOcrModelPackage(
            manifest,
            pathsByRole["detection"],
            pathsByRole["recognition"],
            dictionary,
            hashesByRole);
    }

    private static void ValidateManifest(PpOcrModelPackageManifest manifest)
    {
        if (manifest.ManifestVersion != SupportedManifestVersion
            || !manifest.Id.Equals("pp-ocrv6-small", StringComparison.Ordinal)
            || !manifest.Backend.Equals("onnxruntime", StringComparison.Ordinal)
            || !manifest.Format.Equals("onnx", StringComparison.Ordinal)
            || !manifest.Architecture.Equals("PP-OCRv6-small", StringComparison.Ordinal)
            || !manifest.Capabilities.Contains("ocr", StringComparer.Ordinal)
            || manifest.Files.Length != 3
            || manifest.InstalledBytes is <= 0 or > MaximumInstalledBytes
            || manifest.DownloadBytes <= 0
            || manifest.MinRamBytes <= 0
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.License)
            || string.IsNullOrWhiteSpace(manifest.RecommendedHardware)
            || string.IsNullOrWhiteSpace(manifest.OutputSchemaVersion)
            || manifest.InputLanguages.Length == 0
            || manifest.OutputLanguages.Length == 0
            || manifest.Scripts.Length == 0
            || manifest.InputLanguages.Any(language => !IsBcp47Like(language))
            || manifest.OutputLanguages.Any(language => !IsBcp47Like(language))
            || manifest.Scripts.Any(script =>
                script.Length != 4 || script.Any(character => !char.IsAsciiLetter(character))))
        {
            throw new InvalidDataException("The model manifest is not a supported PP-OCRv6-small package.");
        }

        if (!Uri.TryCreate(manifest.SourceUrl, UriKind.Absolute, out var sourceUri)
            || sourceUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The model manifest source URL must use HTTPS.");
        }

        var requiredRoles = new[] { "detection", "recognition", "dictionary" };
        if (requiredRoles.Any(role => !manifest.Files.Any(file => file.Role == role))
            || manifest.Files.Any(file =>
                !requiredRoles.Contains(file.Role, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(file.Role)
                || string.IsNullOrWhiteSpace(file.Path)
                || file.Sha256.Length != 64
                || file.Sha256.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character))
                || (file.Role is "detection" or "recognition"
                    && !Path.GetExtension(file.Path).Equals(".onnx", StringComparison.OrdinalIgnoreCase))
                || (file.Role == "dictionary"
                    && !Path.GetExtension(file.Path).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetExtension(file.Path).Equals(".yml", StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException("The model manifest file list is invalid.");
        }

        var signature = manifest.InputSignature;
        if (string.IsNullOrWhiteSpace(signature.Detection.InputName)
            || string.IsNullOrWhiteSpace(signature.Detection.OutputName)
            || string.IsNullOrWhiteSpace(signature.Recognition.InputName)
            || string.IsNullOrWhiteSpace(signature.Recognition.OutputName)
            || signature.DetectionMaxSideLength is < 320 or > 2048
            || signature.DetectionMaxSideLength % 32 != 0
            || signature.DetectionThreshold is <= 0 or >= 1
            || signature.BoxThreshold is <= 0 or >= 1
            || signature.RecognitionHeight is < 16 or > 128
            || signature.RecognitionWidth is < 32 or > 2048
            || signature.CtcBlankIndex != 0)
        {
            throw new InvalidDataException("The model manifest input signature is invalid.");
        }
    }

    private static async Task<List<string>> ReadDictionaryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return lines.Where(value => value.Length > 0).ToList();
        }

        var dictionary = new List<string>();
        var inDictionary = false;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inDictionary)
            {
                inDictionary = line.Equals("  character_dict:", StringComparison.Ordinal);
                continue;
            }

            if (!line.StartsWith("  - ", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    break;
                }

                continue;
            }

            var scalar = line[4..];
            string value;
            if (scalar.Length >= 2 && scalar[0] == '\'' && scalar[^1] == '\'')
            {
                value = scalar[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }
            else if (scalar.Length >= 2 && scalar[0] == '"' && scalar[^1] == '"')
            {
                value = JsonSerializer.Deserialize<string>(scalar)
                    ?? throw new InvalidDataException("The OCR recognition dictionary contains an invalid value.");
            }
            else
            {
                value = scalar;
            }

            if (value.Length > 0)
            {
                dictionary.Add(value);
            }
        }

        if (dictionary.Count == 0)
        {
            throw new InvalidDataException("The OCR recognition dictionary is missing from its YAML file.");
        }

        return dictionary;
    }

    private static string ResolvePackagePath(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException("Model package paths must be relative.");
        }

        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A model package path escapes the package directory.");
        }

        RejectReparseSegments(root, path);
        return path;
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Model packages cannot contain reparse points.");
            }
        }
    }

    private static void RejectReparseSegments(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static bool IsBcp47Like(string languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag)
            || languageTag.Length > 64
            || languageTag.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            return false;
        }

        var segments = languageTag.Split('-', StringSplitOptions.None);
        return segments.Length > 0
            && segments[0].Length is >= 2 and <= 8
            && segments[0].All(char.IsAsciiLetter)
            && segments.Skip(1).All(segment =>
                segment.Length is >= 1 and <= 8
                && segment.All(char.IsAsciiLetterOrDigit));
    }
}
