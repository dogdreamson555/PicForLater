using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.Library;

public enum LocalSendInboxImportStatus
{
    Imported = 1,
    Duplicate = 2,
    Invalid = 3,
    Unsupported = 4,
    RetryPending = 5,
    Rejected = 6,
    StalePartialRemoved = 7,
    ActivePartialSkipped = 8,
}

public sealed record LocalSendInboxImportResult(
    LocalSendInboxImportStatus Status,
    Guid? ImageItemId = null,
    bool InboxFileRemoved = false,
    string? ErrorCode = null);

public sealed record LocalSendInboxRecoveryResult(
    IReadOnlyList<LocalSendInboxImportResult> Items)
{
    public int ImportedCount => Items.Count(item => item.Status == LocalSendInboxImportStatus.Imported);

    public int DuplicateCount => Items.Count(item => item.Status == LocalSendInboxImportStatus.Duplicate);

    public int RetryPendingCount => Items.Count(item => item.Status == LocalSendInboxImportStatus.RetryPending);
}

public sealed class LocalSendInboxImportService
{
    public const int MaximumInboxFileNameLength = 120;
    public static readonly TimeSpan StalePartialFileAge = TimeSpan.FromHours(24);

    private const string PartialMarker = ".part-";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InboxGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _gate;
    private readonly IImageImportService _imageImporter;
    private readonly string _inboxDirectoryPath;
    private readonly AppDataPaths _paths;
    private readonly TimeProvider _timeProvider;

    public LocalSendInboxImportService(
        AppDataPaths paths,
        IImageImportService imageImporter,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _imageImporter = imageImporter ?? throw new ArgumentNullException(nameof(imageImporter));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _inboxDirectoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(paths.LocalSendInboxDirectoryPath));
        _gate = InboxGates.GetOrAdd(
            _inboxDirectoryPath,
            static _ => new SemaphoreSlim(1, 1));

        EnsureInboxDirectoryIsSafe();
    }

    public static string CreateTargetFileName(string senderFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderFileName);
        var leafName = GetLastPathComponent(senderFileName.Trim());
        var extension = NormalizeExtension(Path.GetExtension(leafName))
            ?? throw new ArgumentException(
                "Only PNG, JPEG, and WebP target names are supported.",
                nameof(senderFileName));
        var baseName = Path.GetFileNameWithoutExtension(leafName);
        var sanitizedBaseName = SanitizeBaseName(baseName)
            .Normalize(NormalizationForm.FormC);
        if (IsReservedWindowsName(sanitizedBaseName))
        {
            sanitizedBaseName = "_" + sanitizedBaseName;
        }

        var prefix = $"{Guid.NewGuid():N}-";
        var maximumBaseNameLength = MaximumInboxFileNameLength - prefix.Length - extension.Length;
        sanitizedBaseName = TruncateWithoutSplittingRunes(
            sanitizedBaseName,
            maximumBaseNameLength).TrimEnd(' ', '.');
        if (sanitizedBaseName.Length == 0)
        {
            sanitizedBaseName = "image";
        }

        return prefix + sanitizedBaseName + extension;
    }

    public async Task<LocalSendInboxImportResult> ImportAsync(
        string absolutePath,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInboxDirectoryIsSafe();
            return await ImportCoreAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalSendInboxRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInboxDirectoryIsSafe();
            var paths = Directory.EnumerateFileSystemEntries(
                    _inboxDirectoryPath,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, PathComparer)
                .ToArray();
            var results = new List<LocalSendInboxImportResult>(paths.Length);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LocalSendInboxImportResult result;
                try
                {
                    if (IsPartialFileName(Path.GetFileName(path)))
                    {
                        result = RecoverPartialFile(path);
                    }
                    else
                    {
                        result = await ImportCoreAsync(path, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    result = new(
                        LocalSendInboxImportStatus.RetryPending,
                        ErrorCode: "InboxRecoveryItemFailed");
                }

                results.Add(result);
            }

            return new(results);
        }
        finally
        {
            _gate.Release();
        }
    }

    private StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private async Task<LocalSendInboxImportResult> ImportCoreAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        var validation = ValidateCandidate(absolutePath);
        if (!validation.IsValid)
        {
            return new(
                validation.ErrorCode == "InboxFileAccessFailed"
                    ? LocalSendInboxImportStatus.RetryPending
                    : LocalSendInboxImportStatus.Rejected,
                ErrorCode: validation.ErrorCode);
        }

        var fileName = Path.GetFileName(validation.FullPath);
        if (IsPartialFileName(fileName))
        {
            return new(
                LocalSendInboxImportStatus.Rejected,
                ErrorCode: "InboxPartialFileRejected");
        }

        var expectedFormat = GetExpectedFormat(fileName);
        if (expectedFormat is null)
        {
            return RemoveRejectedFile(
                validation.FullPath,
                LocalSendInboxImportStatus.Unsupported,
                "UnsupportedImageExtension");
        }

        try
        {
            ImageImportResult importResult;
            await using (var stream = new FileStream(
                             validation.FullPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.Open,
                                 Access = FileAccess.Read,
                                 Share = FileShare.Read,
                                 BufferSize = 128 * 1024,
                                 Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                             }))
            {
                var reopenedValidation = ValidateCandidate(validation.FullPath);
                if (!reopenedValidation.IsValid)
                {
                    return new(
                        LocalSendInboxImportStatus.Rejected,
                        ErrorCode: reopenedValidation.ErrorCode);
                }

                importResult = await _imageImporter.ImportAsync(
                    stream,
                    RestoreOriginalFileName(fileName),
                    ImageSourceKind.LocalSend,
                    expectedFormat,
                    cancellationToken).ConfigureAwait(false);
            }

            var status = importResult.Status switch
            {
                ImageImportStatus.Imported => LocalSendInboxImportStatus.Imported,
                ImageImportStatus.Duplicate => LocalSendInboxImportStatus.Duplicate,
                _ => LocalSendInboxImportStatus.RetryPending,
            };
            if (status == LocalSendInboxImportStatus.RetryPending)
            {
                return new(status, ErrorCode: "InboxImportResultInvalid");
            }

            return RemoveImportedFile(validation.FullPath, status, importResult.ImageItemId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ImageImportException exception) when (IsInvalidImageError(exception.ErrorCode))
        {
            return RemoveRejectedFile(
                validation.FullPath,
                LocalSendInboxImportStatus.Invalid,
                exception.ErrorCode);
        }
        catch (ImageImportException exception)
        {
            return new(
                LocalSendInboxImportStatus.RetryPending,
                ErrorCode: exception.ErrorCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(
                LocalSendInboxImportStatus.RetryPending,
                ErrorCode: "InboxFileAccessFailed");
        }
        catch (Exception)
        {
            return new(
                LocalSendInboxImportStatus.RetryPending,
                ErrorCode: "InboxImportFailed");
        }
    }

    private LocalSendInboxImportResult RecoverPartialFile(string absolutePath)
    {
        var validation = ValidateCandidate(absolutePath);
        if (!validation.IsValid)
        {
            return new(
                validation.ErrorCode == "InboxFileAccessFailed"
                    ? LocalSendInboxImportStatus.RetryPending
                    : LocalSendInboxImportStatus.Rejected,
                ErrorCode: validation.ErrorCode);
        }

        DateTimeOffset lastWriteAtUtc;
        try
        {
            lastWriteAtUtc = File.GetLastWriteTimeUtc(validation.FullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(
                LocalSendInboxImportStatus.RetryPending,
                ErrorCode: "PartialFileTimestampUnavailable");
        }

        if (_timeProvider.GetUtcNow() - lastWriteAtUtc <= StalePartialFileAge)
        {
            return new(LocalSendInboxImportStatus.ActivePartialSkipped);
        }

        var removed = TryDeleteCandidate(validation.FullPath);
        return removed
            ? new(
                LocalSendInboxImportStatus.StalePartialRemoved,
                InboxFileRemoved: true)
            : new(
                LocalSendInboxImportStatus.RetryPending,
                ErrorCode: "StalePartialCleanupFailed");
    }

    private LocalSendInboxImportResult RemoveImportedFile(
        string absolutePath,
        LocalSendInboxImportStatus status,
        Guid imageItemId)
    {
        var removed = TryDeleteCandidate(absolutePath);
        return new(
            status,
            imageItemId,
            removed,
            removed ? null : "InboxFileCleanupFailed");
    }

    private LocalSendInboxImportResult RemoveRejectedFile(
        string absolutePath,
        LocalSendInboxImportStatus status,
        string errorCode)
    {
        var removed = TryDeleteCandidate(absolutePath);
        return new(
            status,
            InboxFileRemoved: removed,
            ErrorCode: removed ? errorCode : "InboxFileCleanupFailed");
    }

    private bool TryDeleteCandidate(string absolutePath)
    {
        try
        {
            var validation = ValidateCandidate(absolutePath, allowMissing: true);
            if (!validation.IsValid)
            {
                return false;
            }

            if (!File.Exists(validation.FullPath) && !Directory.Exists(validation.FullPath))
            {
                return true;
            }

            File.Delete(validation.FullPath);
            return !File.Exists(validation.FullPath) && !Directory.Exists(validation.FullPath);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    private CandidateValidation ValidateCandidate(string absolutePath, bool allowMissing = false)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath))
        {
            return CandidateValidation.Rejected("InboxPathRejected");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return CandidateValidation.Rejected("InboxPathRejected");
        }

        var parentPath = Path.GetDirectoryName(fullPath);
        if (parentPath is null || !PathComparer.Equals(
                Path.TrimEndingDirectorySeparator(parentPath),
                _inboxDirectoryPath))
        {
            return CandidateValidation.Rejected("InboxPathRejected");
        }

        var fileName = Path.GetFileName(fullPath);
        if (!IsSafeLeafFileName(fileName))
        {
            return CandidateValidation.Rejected("InboxFileNameRejected");
        }

        try
        {
            _paths.EnsureSafePath(fullPath);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return allowMissing
                    ? CandidateValidation.Accepted(fullPath)
                    : CandidateValidation.Rejected("InboxFileNotFound");
            }

            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return CandidateValidation.Rejected("InboxReparsePointRejected");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                return CandidateValidation.Rejected("InboxDirectoryRejected");
            }

            return CandidateValidation.Accepted(fullPath);
        }
        catch (InvalidOperationException)
        {
            return CandidateValidation.Rejected("InboxReparsePointRejected");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CandidateValidation.Rejected("InboxFileAccessFailed");
        }
    }

    private void EnsureInboxDirectoryIsSafe()
    {
        _paths.EnsureCreated();
        _paths.EnsureSafePath(_inboxDirectoryPath);
        var attributes = File.GetAttributes(_inboxDirectoryPath);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The LocalSend Inbox directory is not safe to use.");
        }
    }

    private static bool IsInvalidImageError(string errorCode) => errorCode is
        "InvalidImage" or "FileTypeMismatch" or "ImageDimensionsUnsupported";

    private static bool IsPartialFileName(string fileName)
    {
        // LocalSendDotNet writes <destination>.part-<Guid:N> and atomically renames
        // it to the destination only after the transfer has completed.
        var markerIndex = fileName.LastIndexOf(PartialMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var token = fileName.AsSpan(markerIndex + PartialMarker.Length);
        if (token.Length != 32)
        {
            return false;
        }

        foreach (var character in token)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeLeafFileName(string fileName)
    {
        var maximumLength = IsPartialFileName(fileName)
            ? MaximumInboxFileNameLength + PartialMarker.Length + 32
            : MaximumInboxFileNameLength;
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > maximumLength
            || fileName.EndsWith(' ')
            || fileName.EndsWith('.'))
        {
            return false;
        }

        foreach (var rune in fileName.EnumerateRunes())
        {
            var value = rune.Value;
            if (Rune.IsControl(rune)
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
                || value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                return false;
            }
        }

        return !IsReservedWindowsName(fileName);
    }

    private static bool IsReservedWindowsName(string fileName)
    {
        var deviceName = fileName.Split('.', 2)[0];
        return ReservedWindowsNames.Contains(deviceName);
    }

    private static ManagedImageFormat? GetExpectedFormat(string fileName) =>
        NormalizeExtension(Path.GetExtension(fileName)) switch
        {
            ".png" => ManagedImageFormat.Png,
            ".jpg" => ManagedImageFormat.Jpeg,
            ".webp" => ManagedImageFormat.WebP,
            _ => null,
        };

    private static string? NormalizeExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => ".png",
        ".jpg" or ".jpeg" => ".jpg",
        ".webp" => ".webp",
        _ => null,
    };

    private static string RestoreOriginalFileName(string inboxFileName)
    {
        if (inboxFileName.Length > 33
            && inboxFileName[32] == '-'
            && Guid.TryParseExact(inboxFileName[..32], "N", out _))
        {
            return inboxFileName[33..];
        }

        return inboxFileName;
    }

    private static string GetLastPathComponent(string senderFileName)
    {
        var normalized = senderFileName.Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
    }

    private static string SanitizeBaseName(string baseName)
    {
        var builder = new StringBuilder(baseName.Length);
        foreach (var rune in baseName.EnumerateRunes())
        {
            var value = rune.Value;
            var invalid = Rune.IsControl(rune)
                          || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
                          || value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';
            if (invalid)
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(rune);
            }
        }

        var sanitized = builder.ToString().Trim().TrimEnd(' ', '.');
        return sanitized.Length == 0 ? "image" : sanitized;
    }

    private static string TruncateWithoutSplittingRunes(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var builder = new StringBuilder(maximumLength);
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maximumLength)
            {
                break;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }

    private sealed record CandidateValidation(bool IsValid, string FullPath, string? ErrorCode)
    {
        public static CandidateValidation Accepted(string fullPath) => new(true, fullPath, null);

        public static CandidateValidation Rejected(string errorCode) => new(false, string.Empty, errorCode);
    }
}
