using System.Text.Json;

namespace PicForLater.App.Services;

internal sealed class LocalPreferenceStore
{
    private const long MaximumSettingsFileLength = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly PicForLater.Infrastructure.Storage.AppDataPaths _paths;
    private readonly string _settingsFilePath;
    private Dictionary<string, int>? _values;

    private LocalPreferenceStore(PicForLater.Infrastructure.Storage.AppDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settingsFilePath = paths.SettingsFilePath;
    }

    public static LocalPreferenceStore Instance { get; } =
        new(AppRuntimePaths.Paths);

    public bool TryGetInt32(string key, out int value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            return GetValues().TryGetValue(key, out value);
        }
    }

    public void SetInt32(string key, int value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var updatedValues = new Dictionary<string, int>(GetValues(), StringComparer.Ordinal)
            {
                [key] = value,
            };
            Save(updatedValues);
            _values = updatedValues;
        }
    }

    private Dictionary<string, int> GetValues()
    {
        if (_values is not null)
        {
            return _values;
        }

        try
        {
            if (File.Exists(_settingsFilePath))
            {
                _paths.EnsureSafePath(_settingsFilePath);
                EnsureNotReparsePoint(_settingsFilePath);
                if (new FileInfo(_settingsFilePath).Length > MaximumSettingsFileLength)
                {
                    throw new InvalidDataException("The settings file is too large.");
                }

                var json = File.ReadAllText(_settingsFilePath);
                _values = JsonSerializer.Deserialize<Dictionary<string, int>>(
                              json,
                              SerializerOptions)
                          ?? new Dictionary<string, int>(StringComparer.Ordinal);
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or InvalidDataException
                                          or InvalidOperationException)
        {
            // Preferences are convenience state, not user content. A corrupt or
            // unreadable file falls back to defaults without blocking the library.
        }

        _values ??= new Dictionary<string, int>(StringComparer.Ordinal);
        return _values;
    }

    private void Save(Dictionary<string, int> values)
    {
        var directory = Path.GetDirectoryName(_settingsFilePath)
            ?? throw new InvalidOperationException("The settings directory is unavailable.");
        _paths.EnsureCreated();
        _paths.EnsureSafePath(directory);
        _paths.EnsureSafePath(_settingsFilePath);
        EnsureNotReparsePoint(directory);
        if (File.Exists(_settingsFilePath))
        {
            EnsureNotReparsePoint(_settingsFilePath);
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(values, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsFilePath, overwrite: true);
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

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The settings path cannot be a reparse point.");
        }
    }
}
