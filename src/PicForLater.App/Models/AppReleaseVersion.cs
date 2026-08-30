using System.Globalization;

namespace PicForLater.App.Models;

/// <summary>
/// A PicForLater release version containing exactly major, minor, and patch components.
/// </summary>
public readonly record struct AppReleaseVersion : IComparable<AppReleaseVersion>
{
    public AppReleaseVersion(int major, int minor, int patch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public static bool TryParseLocal(
        string? informationalVersion,
        out AppReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(informationalVersion))
        {
            return false;
        }

        var versionSpan = informationalVersion.AsSpan();
        var metadataSeparator = versionSpan.IndexOf('+');
        if (metadataSeparator >= 0)
        {
            if (metadataSeparator == versionSpan.Length - 1
                || !IsValidBuildMetadata(versionSpan[(metadataSeparator + 1)..]))
            {
                return false;
            }

            versionSpan = versionSpan[..metadataSeparator];
        }

        return TryParseCore(versionSpan, out version);
    }

    public static bool TryParseReleaseTag(string? tag, out AppReleaseVersion version)
    {
        version = default;
        return tag is { Length: > 1 }
            && tag[0] == 'v'
            && TryParseCore(tag.AsSpan(1), out version);
    }

    public int CompareTo(AppReleaseVersion other)
    {
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        var minorComparison = Minor.CompareTo(other.Minor);
        return minorComparison != 0
            ? minorComparison
            : Patch.CompareTo(other.Patch);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch}");

    private static bool TryParseCore(
        ReadOnlySpan<char> value,
        out AppReleaseVersion version)
    {
        version = default;
        var firstSeparator = value.IndexOf('.');
        if (firstSeparator <= 0)
        {
            return false;
        }

        var remaining = value[(firstSeparator + 1)..];
        var secondSeparator = remaining.IndexOf('.');
        if (secondSeparator <= 0 || remaining[(secondSeparator + 1)..].Contains('.'))
        {
            return false;
        }

        var major = value[..firstSeparator];
        var minor = remaining[..secondSeparator];
        var patch = remaining[(secondSeparator + 1)..];
        if (!TryParseComponent(major, out var majorValue)
            || !TryParseComponent(minor, out var minorValue)
            || !TryParseComponent(patch, out var patchValue))
        {
            return false;
        }

        version = new AppReleaseVersion(majorValue, minorValue, patchValue);
        return true;
    }

    private static bool TryParseComponent(ReadOnlySpan<char> value, out int component)
    {
        component = default;
        if (value.IsEmpty)
        {
            return false;
        }

        if (value.Length > 1 && value[0] == '0')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out component);
    }

    private static bool IsValidBuildMetadata(ReadOnlySpan<char> metadata)
    {
        var componentLength = 0;
        foreach (var character in metadata)
        {
            if (character == '.')
            {
                if (componentLength == 0)
                {
                    return false;
                }

                componentLength = 0;
                continue;
            }

            if (!IsAsciiLetterOrDigit(character) && character != '-')
            {
                return false;
            }

            componentLength++;
        }

        return componentLength > 0;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z';
}
