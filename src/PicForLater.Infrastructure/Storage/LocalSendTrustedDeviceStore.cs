using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PicForLater.Infrastructure.Storage;

public sealed record LocalSendTrustedDevice(
    string Fingerprint,
    string DisplayName,
    DateTimeOffset FirstPairedAtUtc,
    DateTimeOffset? LastReceivedAtUtc);

public sealed class LocalSendTrustedDeviceStore
{
    public const int MaximumDeviceCount = 64;
    public const int MaximumDisplayNameLength = 80;
    public const long MaximumFileLength = 64 * 1024;

    private const int SchemaVersion = 1;
    private const string EmptyDisplayName = "LocalSend device";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate;
    private readonly AppDataPaths _paths;
    private readonly TimeProvider _timeProvider;

    public LocalSendTrustedDeviceStore(AppDataPaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gate = StoreGates.GetOrAdd(
            Path.GetFullPath(paths.LocalSendTrustedDevicesFilePath),
            static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<LocalSendTrustedDevice>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalSendTrustedDevice?> FindAsync(
        string? fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeFingerprint(fingerprint, out var normalizedFingerprint))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false);
            return devices.FirstOrDefault(device =>
                StringComparer.Ordinal.Equals(device.Fingerprint, normalizedFingerprint));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalSendTrustedDevice> AddAsync(
        string fingerprint,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var normalizedFingerprint = NormalizeFingerprintArgument(fingerprint);
        var normalizedDisplayName = NormalizeDisplayName(displayName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var existingIndex = devices.FindIndex(device =>
                StringComparer.Ordinal.Equals(device.Fingerprint, normalizedFingerprint));
            LocalSendTrustedDevice trustedDevice;
            if (existingIndex >= 0)
            {
                trustedDevice = devices[existingIndex] with { DisplayName = normalizedDisplayName };
                devices[existingIndex] = trustedDevice;
            }
            else
            {
                if (devices.Count >= MaximumDeviceCount)
                {
                    throw new InvalidDataException("The LocalSend trusted-device limit has been reached.");
                }

                trustedDevice = new(
                    normalizedFingerprint,
                    normalizedDisplayName,
                    _timeProvider.GetUtcNow().ToUniversalTime(),
                    LastReceivedAtUtc: null);
                devices.Add(trustedDevice);
            }

            await WriteAllCoreAsync(devices, cancellationToken).ConfigureAwait(false);
            return trustedDevice;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> MarkReceivedAsync(
        string fingerprint,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var normalizedFingerprint = NormalizeFingerprintArgument(fingerprint);
        var normalizedDisplayName = NormalizeDisplayName(displayName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var existingIndex = devices.FindIndex(device =>
                StringComparer.Ordinal.Equals(device.Fingerprint, normalizedFingerprint));
            if (existingIndex < 0)
            {
                return false;
            }

            var existing = devices[existingIndex];
            var receivedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            if (receivedAtUtc < existing.FirstPairedAtUtc)
            {
                receivedAtUtc = existing.FirstPairedAtUtc;
            }

            if (existing.LastReceivedAtUtc is { } previousReceivedAtUtc
                && receivedAtUtc < previousReceivedAtUtc)
            {
                receivedAtUtc = previousReceivedAtUtc;
            }

            devices[existingIndex] = existing with
            {
                DisplayName = normalizedDisplayName,
                LastReceivedAtUtc = receivedAtUtc,
            };
            await WriteAllCoreAsync(devices, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        var normalizedFingerprint = NormalizeFingerprintArgument(fingerprint);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = (await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var removed = devices.RemoveAll(device =>
                StringComparer.Ordinal.Equals(device.Fingerprint, normalizedFingerprint)) > 0;
            if (!removed)
            {
                return false;
            }

            await WriteAllCoreAsync(devices, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<LocalSendTrustedDevice>> ReadAllCoreAsync(
        CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        _paths.EnsureSafePath(_paths.LocalSendTrustedDevicesFilePath);
        if (!File.Exists(_paths.LocalSendTrustedDevicesFilePath))
        {
            return [];
        }

        _paths.EnsureSafePath(_paths.LocalSendTrustedDevicesFilePath);
        await using var stream = new FileStream(
            _paths.LocalSendTrustedDevicesFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is 0 or > MaximumFileLength)
        {
            throw new InvalidDataException("The LocalSend trusted-device file has an invalid size.");
        }

        TrustedDevicesDocument document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<TrustedDevicesDocument>(
                           stream,
                           SerializerOptions,
                           cancellationToken)
                       .ConfigureAwait(false)
                       ?? throw new InvalidDataException("The LocalSend trusted-device file is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The LocalSend trusted-device file is invalid.", exception);
        }

        return ValidateDocument(document);
    }

    private async Task WriteAllCoreAsync(
        IReadOnlyCollection<LocalSendTrustedDevice> devices,
        CancellationToken cancellationToken)
    {
        if (devices.Count > MaximumDeviceCount)
        {
            throw new InvalidDataException("The LocalSend trusted-device limit has been reached.");
        }

        _paths.EnsureCreated();
        var directory = Path.GetDirectoryName(_paths.LocalSendTrustedDevicesFilePath)
            ?? throw new InvalidOperationException("The LocalSend trusted-device directory is unavailable.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_paths.LocalSendTrustedDevicesFilePath)}.{Guid.NewGuid():N}.tmp");
        _paths.EnsureSafePath(directory);
        _paths.EnsureSafePath(_paths.LocalSendTrustedDevicesFilePath);
        _paths.EnsureSafePath(temporaryPath);

        var document = new TrustedDevicesDocument
        {
            SchemaVersion = SchemaVersion,
            Devices = devices
                .OrderBy(device => device.FirstPairedAtUtc)
                .ThenBy(device => device.Fingerprint, StringComparer.Ordinal)
                .Select(device => new TrustedDeviceDocumentEntry
                {
                    Fingerprint = device.Fingerprint,
                    DisplayName = device.DisplayName,
                    FirstPairedAtUtc = device.FirstPairedAtUtc.ToUniversalTime(),
                    LastReceivedAtUtc = device.LastReceivedAtUtc?.ToUniversalTime(),
                })
                .ToList(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        if (bytes.LongLength > MaximumFileLength)
        {
            throw new InvalidDataException("The LocalSend trusted-device file would exceed its size limit.");
        }

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _paths.EnsureSafePath(temporaryPath);
            _paths.EnsureSafePath(_paths.LocalSendTrustedDevicesFilePath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _paths.LocalSendTrustedDevicesFilePath, overwrite: true);
            _paths.EnsureSafePath(_paths.LocalSendTrustedDevicesFilePath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static IReadOnlyList<LocalSendTrustedDevice> ValidateDocument(
        TrustedDevicesDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Devices is null)
        {
            throw new InvalidDataException("The LocalSend trusted-device schema is unsupported.");
        }

        if (document.Devices.Count > MaximumDeviceCount)
        {
            throw new InvalidDataException("The LocalSend trusted-device file has too many records.");
        }

        var devices = new List<LocalSendTrustedDevice>(document.Devices.Count);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Devices)
        {
            if (entry is null
                || !TryNormalizeFingerprint(entry.Fingerprint, out var fingerprint)
                || !fingerprints.Add(fingerprint)
                || entry.DisplayName is null
                || entry.FirstPairedAtUtc == default
                || entry.FirstPairedAtUtc.Offset != TimeSpan.Zero
                || entry.LastReceivedAtUtc is { Offset: var offset } && offset != TimeSpan.Zero
                || entry.LastReceivedAtUtc < entry.FirstPairedAtUtc)
            {
                throw new InvalidDataException("The LocalSend trusted-device file contains an invalid record.");
            }

            devices.Add(new(
                fingerprint,
                NormalizeDisplayName(entry.DisplayName),
                entry.FirstPairedAtUtc,
                entry.LastReceivedAtUtc));
        }

        return devices
            .OrderBy(device => device.FirstPairedAtUtc)
            .ThenBy(device => device.Fingerprint, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeFingerprintArgument(string fingerprint)
    {
        if (!TryNormalizeFingerprint(fingerprint, out var normalizedFingerprint))
        {
            throw new ArgumentException(
                "A LocalSend fingerprint must contain exactly 64 hexadecimal characters.",
                nameof(fingerprint));
        }

        return normalizedFingerprint;
    }

    private static bool TryNormalizeFingerprint(string? fingerprint, out string normalizedFingerprint)
    {
        if (fingerprint is null
            || fingerprint.Length != 64
            || !fingerprint.All(char.IsAsciiHexDigit))
        {
            normalizedFingerprint = string.Empty;
            return false;
        }

        normalizedFingerprint = fingerprint.ToLowerInvariant();
        return true;
    }

    private static string NormalizeDisplayName(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);

        var builder = new StringBuilder(MaximumDisplayNameLength);
        var needsSpace = false;
        var scalarCount = 0;
        foreach (var rune in displayName.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsControl(rune) || category == UnicodeCategory.Format)
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                needsSpace = builder.Length > 0;
                continue;
            }

            if (needsSpace)
            {
                if (scalarCount + 1 >= MaximumDisplayNameLength)
                {
                    break;
                }

                builder.Append(' ');
                scalarCount++;
                needsSpace = false;
            }

            if (scalarCount >= MaximumDisplayNameLength)
            {
                break;
            }

            builder.Append(rune);
            scalarCount++;
        }

        return builder.Length == 0 ? EmptyDisplayName : builder.ToString();
    }

    private sealed class TrustedDevicesDocument
    {
        public int SchemaVersion { get; init; }

        public List<TrustedDeviceDocumentEntry>? Devices { get; init; }
    }

    private sealed class TrustedDeviceDocumentEntry
    {
        public string? Fingerprint { get; init; }

        public string? DisplayName { get; init; }

        public DateTimeOffset FirstPairedAtUtc { get; init; }

        public DateTimeOffset? LastReceivedAtUtc { get; init; }
    }
}
