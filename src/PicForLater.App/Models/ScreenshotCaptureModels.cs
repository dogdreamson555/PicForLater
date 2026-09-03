using System.Globalization;

namespace PicForLater.App.Models;

[Flags]
public enum ScreenshotHotKeyModifiers
{
    None = 0,
    Alt = 1 << 0,
    Control = 1 << 1,
    Shift = 1 << 2,
    Win = 1 << 3,
}

public enum ScreenshotHotKeyKey
{
    Tab = 0x09,
    CapitalLock = 0x14,
    D0 = 0x30,
    D1 = 0x31,
    D2 = 0x32,
    D3 = 0x33,
    D4 = 0x34,
    D5 = 0x35,
    D6 = 0x36,
    D7 = 0x37,
    D8 = 0x38,
    D9 = 0x39,
    A = 0x41,
    B = 0x42,
    C = 0x43,
    D = 0x44,
    E = 0x45,
    F = 0x46,
    G = 0x47,
    H = 0x48,
    I = 0x49,
    J = 0x4A,
    K = 0x4B,
    L = 0x4C,
    M = 0x4D,
    N = 0x4E,
    O = 0x4F,
    P = 0x50,
    Q = 0x51,
    R = 0x52,
    S = 0x53,
    T = 0x54,
    U = 0x55,
    V = 0x56,
    W = 0x57,
    X = 0x58,
    Y = 0x59,
    Z = 0x5A,
    NumberPad0 = 0x60,
    NumberPad1 = 0x61,
    NumberPad2 = 0x62,
    NumberPad3 = 0x63,
    NumberPad4 = 0x64,
    NumberPad5 = 0x65,
    NumberPad6 = 0x66,
    NumberPad7 = 0x67,
    NumberPad8 = 0x68,
    NumberPad9 = 0x69,
    Decimal = 0x6E,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,
    OemSemicolon = 0xBA,
    OemComma = 0xBC,
    OemPeriod = 0xBE,
    OemQuestion = 0xBF,
    OemOpenBrackets = 0xDB,
    OemPipe = 0xDC,
    OemCloseBrackets = 0xDD,
    OemQuotes = 0xDE,
}

public readonly record struct ScreenshotHotKey
{
    private const ScreenshotHotKeyModifiers AllowedModifiers =
        ScreenshotHotKeyModifiers.Alt |
        ScreenshotHotKeyModifiers.Control |
        ScreenshotHotKeyModifiers.Shift |
        ScreenshotHotKeyModifiers.Win;

    public ScreenshotHotKey(ScreenshotHotKeyModifiers modifiers, ScreenshotHotKeyKey key)
    {
        if (!IsValid(modifiers, key))
        {
            throw new ArgumentOutOfRangeException(nameof(modifiers), "The screenshot hotkey is not supported.");
        }

        Modifiers = modifiers;
        Key = key;
    }

    public static ScreenshotHotKey Default { get; } = new(
        ScreenshotHotKeyModifiers.Win | ScreenshotHotKeyModifiers.Alt,
        ScreenshotHotKeyKey.X);

    public ScreenshotHotKeyModifiers Modifiers { get; }

    public ScreenshotHotKeyKey Key { get; }

    public int Pack() => checked(((int)Modifiers << 16) | (int)Key);

    public static bool TryUnpack(int packedValue, out ScreenshotHotKey hotKey)
    {
        var modifiers = (ScreenshotHotKeyModifiers)((uint)packedValue >> 16);
        var key = (ScreenshotHotKeyKey)(packedValue & 0xFFFF);
        if (!IsValid(modifiers, key))
        {
            hotKey = default;
            return false;
        }

        hotKey = new ScreenshotHotKey(modifiers, key);
        return true;
    }

    public static bool IsValid(ScreenshotHotKeyModifiers modifiers, ScreenshotHotKeyKey key)
    {
        if ((modifiers & ~AllowedModifiers) != 0)
        {
            return false;
        }

        return IsSupportedKey(key);
    }

    public static bool IsSupportedKey(ScreenshotHotKeyKey key)
    {
        int virtualKey = (int)key;
        return key is ScreenshotHotKeyKey.Tab or
                ScreenshotHotKeyKey.CapitalLock or
                ScreenshotHotKeyKey.Decimal or
                ScreenshotHotKeyKey.OemSemicolon or
                ScreenshotHotKeyKey.OemComma or
                ScreenshotHotKeyKey.OemPeriod or
                ScreenshotHotKeyKey.OemQuestion or
                ScreenshotHotKeyKey.OemOpenBrackets or
                ScreenshotHotKeyKey.OemPipe or
                ScreenshotHotKeyKey.OemCloseBrackets or
                ScreenshotHotKeyKey.OemQuotes ||
            virtualKey is >= (int)ScreenshotHotKeyKey.D0 and <= (int)ScreenshotHotKeyKey.D9 or
                >= (int)ScreenshotHotKeyKey.A and <= (int)ScreenshotHotKeyKey.Z or
                >= (int)ScreenshotHotKeyKey.NumberPad0 and <= (int)ScreenshotHotKeyKey.NumberPad9 or
                >= (int)ScreenshotHotKeyKey.F1 and <= (int)ScreenshotHotKeyKey.F12;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        AddModifier(parts, ScreenshotHotKeyModifiers.Win, "Win");
        AddModifier(parts, ScreenshotHotKeyModifiers.Control, "Ctrl");
        AddModifier(parts, ScreenshotHotKeyModifiers.Alt, "Alt");
        AddModifier(parts, ScreenshotHotKeyModifiers.Shift, "Shift");
        parts.Add(FormatKey(Key, Modifiers));
        return string.Join(" + ", parts);
    }

    private void AddModifier(
        ICollection<string> parts,
        ScreenshotHotKeyModifiers modifier,
        string label)
    {
        if ((Modifiers & modifier) != 0)
        {
            parts.Add(label);
        }
    }

    public static string FormatKey(ScreenshotHotKeyKey key) =>
        FormatKey(key, ScreenshotHotKeyModifiers.None);

    public static string FormatKey(
        ScreenshotHotKeyKey key,
        ScreenshotHotKeyModifiers modifiers)
    {
        int virtualKey = (int)key;
        if (virtualKey is >= (int)ScreenshotHotKeyKey.D0 and <= (int)ScreenshotHotKeyKey.D9)
        {
            return (virtualKey - (int)ScreenshotHotKeyKey.D0).ToString(
                CultureInfo.InvariantCulture);
        }

        if (virtualKey is >= (int)ScreenshotHotKeyKey.A and <= (int)ScreenshotHotKeyKey.Z)
        {
            return ((char)virtualKey).ToString(CultureInfo.InvariantCulture);
        }

        if (virtualKey is >= (int)ScreenshotHotKeyKey.NumberPad0 and
            <= (int)ScreenshotHotKeyKey.NumberPad9)
        {
            return $"Num {virtualKey - (int)ScreenshotHotKeyKey.NumberPad0}";
        }

        if (virtualKey is >= (int)ScreenshotHotKeyKey.F1 and <= (int)ScreenshotHotKeyKey.F12)
        {
            return $"F{virtualKey - (int)ScreenshotHotKeyKey.F1 + 1}";
        }

        bool shifted = modifiers.HasFlag(ScreenshotHotKeyModifiers.Shift);
        return key switch
        {
            ScreenshotHotKeyKey.Tab => "Tab",
            ScreenshotHotKeyKey.CapitalLock => "Caps Lock",
            ScreenshotHotKeyKey.Decimal => "Num .",
            ScreenshotHotKeyKey.OemSemicolon => shifted ? ":" : ";",
            ScreenshotHotKeyKey.OemComma => shifted ? "<" : ",",
            ScreenshotHotKeyKey.OemPeriod => shifted ? ">" : ".",
            ScreenshotHotKeyKey.OemQuestion => shifted ? "?" : "/",
            ScreenshotHotKeyKey.OemOpenBrackets => shifted ? "{" : "[",
            ScreenshotHotKeyKey.OemPipe => shifted ? "|" : "\\",
            ScreenshotHotKeyKey.OemCloseBrackets => shifted ? "}" : "]",
            ScreenshotHotKeyKey.OemQuotes => shifted ? "\"" : "'",
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
    }
}

public enum RegistrationState
{
    Disabled,
    Ready,
    Conflict,
    Faulted,
}

public enum CaptureState
{
    Idle,
    Capturing,
    Importing,
}

public enum CaptureOutcome
{
    Imported,
    Duplicate,
    TimedOut,
    Failed,
}

public enum ScreenshotCaptureFailureKind
{
    InputInjection,
    ClipboardUnavailable,
    UnsupportedClipboardImage,
    InvalidImage,
    Import,
}

public enum ScreenshotSettingsFailureKind
{
    None,
    HotKeyConflict,
    Registration,
    Preference,
    NotStarted,
}

public enum ScreenshotHotKeyRegistrationStatus
{
    Registered,
    Conflict,
    Failed,
}

public enum ScreenshotClipboardImageFormat
{
    Png,
    DibV5,
}

public enum ScreenshotClipboardReadStatus
{
    Image,
    NoImage,
    UnsupportedImage,
    InvalidImage,
    ClipboardUnavailable,
}

public enum ScreenshotImportStatus
{
    Imported,
    Duplicate,
}

public sealed record ScreenshotCapturePreferences(
    bool IsEnabledRequested,
    ScreenshotHotKey HotKey);

public sealed record ScreenshotCaptureSnapshot(
    bool IsEnabledRequested,
    ScreenshotHotKey HotKey,
    RegistrationState RegistrationState,
    CaptureState CaptureState)
{
    public static ScreenshotCaptureSnapshot Default { get; } = new(
        false,
        ScreenshotHotKey.Default,
        RegistrationState.Disabled,
        CaptureState.Idle);
}

public sealed record ScreenshotCaptureResult(
    CaptureOutcome Outcome,
    ScreenshotCaptureFailureKind? FailureKind = null,
    Guid? ImageItemId = null)
{
    public static ScreenshotCaptureResult Imported(Guid imageItemId) =>
        new(CaptureOutcome.Imported, ImageItemId: imageItemId);

    public static ScreenshotCaptureResult Duplicate(Guid imageItemId) =>
        new(CaptureOutcome.Duplicate, ImageItemId: imageItemId);

    public static ScreenshotCaptureResult TimedOut() => new(CaptureOutcome.TimedOut);

    public static ScreenshotCaptureResult Failed(ScreenshotCaptureFailureKind failureKind) =>
        new(CaptureOutcome.Failed, failureKind);
}

public sealed record ScreenshotSettingsOperationResult(
    bool Succeeded,
    ScreenshotSettingsFailureKind FailureKind = ScreenshotSettingsFailureKind.None)
{
    public static ScreenshotSettingsOperationResult Success { get; } = new(true);

    public static ScreenshotSettingsOperationResult Failed(ScreenshotSettingsFailureKind failureKind) =>
        new(false, failureKind);
}

public sealed record ScreenshotClipboardImage
{
    private readonly byte[] _bytes;

    public ScreenshotClipboardImage(
        ScreenshotClipboardImageFormat format,
        byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("The Clipboard image cannot be empty.", nameof(bytes));
        }

        Format = format;
        _bytes = bytes;
    }

    public ScreenshotClipboardImageFormat Format { get; }

    public ReadOnlyMemory<byte> Bytes => _bytes;

    public Stream OpenReadStream() => new MemoryStream(_bytes, writable: false);
}

public readonly record struct ScreenshotForegroundWindowSnapshot(
    nint WindowHandle,
    uint ProcessId)
{
    public bool IsAvailable => WindowHandle != 0;
}

public sealed record ScreenshotClipboardReadResult(
    ScreenshotClipboardReadStatus Status,
    uint SequenceNumber,
    ScreenshotClipboardImage? Image = null)
{
    public static ScreenshotClipboardReadResult FromImage(
        uint sequenceNumber,
        ScreenshotClipboardImage image) =>
        new(
            ScreenshotClipboardReadStatus.Image,
            sequenceNumber,
            image ?? throw new ArgumentNullException(nameof(image)));

    public static ScreenshotClipboardReadResult NoImage(uint sequenceNumber) =>
        new(ScreenshotClipboardReadStatus.NoImage, sequenceNumber);

    public static ScreenshotClipboardReadResult UnsupportedImage(uint sequenceNumber) =>
        new(ScreenshotClipboardReadStatus.UnsupportedImage, sequenceNumber);

    public static ScreenshotClipboardReadResult InvalidImage(uint sequenceNumber) =>
        new(ScreenshotClipboardReadStatus.InvalidImage, sequenceNumber);

    public static ScreenshotClipboardReadResult ClipboardUnavailable { get; } =
        new(ScreenshotClipboardReadStatus.ClipboardUnavailable, 0);
}

public readonly record struct ScreenshotClipboardAccessResult(
    bool IsAvailable,
    uint SequenceNumber)
{
    public static ScreenshotClipboardAccessResult Available(uint sequenceNumber) =>
        new(true, sequenceNumber);

    public static ScreenshotClipboardAccessResult Unavailable { get; } = new(false, 0);
}

public sealed record ScreenshotImportResult(
    ScreenshotImportStatus Status,
    Guid ImageItemId);

public sealed record ScreenshotCaptureOptions
{
    public static ScreenshotCaptureOptions Default { get; } = new();

    public TimeSpan KeyReleaseTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan KeyReleasePollingInterval { get; init; } = TimeSpan.FromMilliseconds(15);

    public TimeSpan ClipboardPollingInterval { get; init; } = TimeSpan.FromMilliseconds(60);

    public TimeSpan CaptureUiExitGracePeriod { get; init; } = TimeSpan.FromMilliseconds(750);

    public TimeSpan CaptureTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        ValidatePositive(KeyReleaseTimeout, nameof(KeyReleaseTimeout));
        ValidatePositive(KeyReleasePollingInterval, nameof(KeyReleasePollingInterval));
        ValidatePositive(ClipboardPollingInterval, nameof(ClipboardPollingInterval));
        ValidatePositive(CaptureUiExitGracePeriod, nameof(CaptureUiExitGracePeriod));
        ValidatePositive(CaptureTimeout, nameof(CaptureTimeout));
        if (CaptureUiExitGracePeriod > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(CaptureUiExitGracePeriod));
        }

        if (CaptureTimeout > TimeSpan.FromSeconds(120))
        {
            throw new ArgumentOutOfRangeException(nameof(CaptureTimeout));
        }
    }

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
