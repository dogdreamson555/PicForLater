using System.Diagnostics.CodeAnalysis;

namespace PicForLater.Core.Images;

/// <summary>
/// A normalized path relative to the application's managed-data root.
/// </summary>
public sealed record ManagedRelativePath
{
    private static readonly char[] ExplicitInvalidWindowsCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    private ManagedRelativePath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ManagedRelativePath Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Replace('\\', '/');
        if (Path.IsPathRooted(value)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Managed paths must be relative file paths.", nameof(value));
        }

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(IsInvalidSegment))
        {
            throw new ArgumentException("The managed path contains an invalid or unsafe segment.", nameof(value));
        }

        return new ManagedRelativePath(string.Join('/', segments));
    }

    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out ManagedRelativePath? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            path = Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool IsUnder(string firstSegment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstSegment);
        return Value.StartsWith(firstSegment + '/', StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Value;

    private static bool IsInvalidSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || segment is "." or ".."
            || segment.EndsWith(' ')
            || segment.EndsWith('.'))
        {
            return true;
        }

        if (segment.Any(character => character < 32)
            || segment.IndexOfAny(ExplicitInvalidWindowsCharacters) >= 0
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return true;
        }

        var deviceName = segment.Split('.', 2)[0];
        return deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || IsNumberedDevice(deviceName, "COM")
            || IsNumberedDevice(deviceName, "LPT");
    }

    private static bool IsNumberedDevice(string value, string prefix)
    {
        return value.Length == 4
            && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && value[3] is >= '1' and <= '9';
    }
}
