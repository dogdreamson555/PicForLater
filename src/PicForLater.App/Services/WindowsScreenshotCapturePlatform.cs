using System.ComponentModel;
using System.Runtime.InteropServices;
using PicForLater.App.Models;

namespace PicForLater.App.Services;

internal sealed class WindowsScreenshotCapturePlatform : IScreenshotCapturePlatform, IDisposable
{
    private const uint ModNoRepeat = 0x4000;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmHotKey = 0x0312;
    private const int ErrorHotKeyAlreadyRegistered = 1409;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkS = 0x53;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;
    private static readonly nuint SubclassId = 0x50464C53;

    private readonly NativeMethods.SubclassProc _subclassProc;
    private readonly uint _ownerThreadId;
    private readonly HashSet<int> _registeredHotKeyIds = [];
    private readonly WindowsClipboardImageReader _clipboardReader;
    private nint _windowHandle;
    private bool _subclassInstalled;
    private bool _disposed;

    private WindowsScreenshotCapturePlatform(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A valid main-window HWND is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _clipboardReader = new WindowsClipboardImageReader(windowHandle);
        _ownerThreadId = NativeMethods.GetWindowThreadProcessId(windowHandle, out _);
        if (_ownerThreadId == 0 || _ownerThreadId != NativeMethods.GetCurrentThreadId())
        {
            throw new InvalidOperationException(
                "The screenshot platform must be created on the HWND owner thread.");
        }

        _subclassProc = WindowSubclassProc;
        if (!NativeMethods.SetWindowSubclass(
                _windowHandle,
                _subclassProc,
                SubclassId,
                referenceData: 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _subclassInstalled = true;
    }

    public event EventHandler<ScreenshotHotKeyPressedEventArgs>? HotKeyPressed;

    internal static IScreenshotCapturePlatform Create(nint windowHandle)
    {
        try
        {
            return new WindowsScreenshotCapturePlatform(windowHandle);
        }
        catch (Win32Exception)
        {
            // Quick Screenshot is optional and defaults off. Failure to install
            // its native boundary must not block access to the local library.
            return new UnavailableScreenshotCapturePlatform();
        }
    }

    public ScreenshotHotKeyRegistrationStatus RegisterHotKey(
        int hotKeyId,
        ScreenshotHotKey hotKey)
    {
        ThrowIfUnavailableOrWrongThread();
        ValidateHotKeyId(hotKeyId);
        if (!ScreenshotHotKey.IsValid(hotKey.Modifiers, hotKey.Key))
        {
            throw new ArgumentOutOfRangeException(nameof(hotKey));
        }

        if (_registeredHotKeyIds.Contains(hotKeyId))
        {
            return ScreenshotHotKeyRegistrationStatus.Failed;
        }

        uint modifiers = GetNativeModifiers(hotKey.Modifiers);
        if (!NativeMethods.RegisterHotKey(
                _windowHandle,
                hotKeyId,
                modifiers,
                checked((uint)hotKey.Key)))
        {
            return Marshal.GetLastWin32Error() == ErrorHotKeyAlreadyRegistered
                ? ScreenshotHotKeyRegistrationStatus.Conflict
                : ScreenshotHotKeyRegistrationStatus.Failed;
        }

        _registeredHotKeyIds.Add(hotKeyId);
        return ScreenshotHotKeyRegistrationStatus.Registered;
    }

    public bool UnregisterHotKey(int hotKeyId)
    {
        ThrowIfUnavailableOrWrongThread();
        ValidateHotKeyId(hotKeyId);
        if (!_registeredHotKeyIds.Contains(hotKeyId))
        {
            return true;
        }

        if (!NativeMethods.UnregisterHotKey(_windowHandle, hotKeyId))
        {
            return false;
        }

        _registeredHotKeyIds.Remove(hotKeyId);
        return true;
    }

    public bool AreCaptureKeysReleased(ScreenshotHotKey hotKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return !IsKeyDown(VkLWin) &&
            !IsKeyDown(VkRWin) &&
            !IsKeyDown(VkControl) &&
            !IsKeyDown(VkMenu) &&
            !IsKeyDown(VkShift) &&
            !IsKeyDown((int)hotKey.Key) &&
            !IsKeyDown(VkS);
    }

    public bool SendScreenshotShortcut()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeMethods.Input[] inputs = CreateScreenshotShortcutInputs();
        uint inserted = NativeMethods.SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<NativeMethods.Input>());
        if (inserted is > 0 and < 6)
        {
            NativeMethods.Input[] cleanupInputs = CreateScreenshotShortcutCleanupInputs();
            _ = NativeMethods.SendInput(
                checked((uint)cleanupInputs.Length),
                cleanupInputs,
                Marshal.SizeOf<NativeMethods.Input>());
        }

        return inserted == inputs.Length;
    }

    public ScreenshotForegroundWindowSnapshot GetForegroundWindowSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        nint windowHandle = NativeMethods.GetForegroundWindow();
        if (windowHandle == 0)
        {
            return default;
        }

        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out uint processId);
        return new ScreenshotForegroundWindowSnapshot(windowHandle, processId);
    }

    public uint GetClipboardSequenceNumber()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeMethods.GetClipboardSequenceNumber();
    }

    public ValueTask<ScreenshotClipboardAccessResult> ProbeClipboardAccessAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _clipboardReader.ProbeAccessAsync(cancellationToken);
    }

    public ValueTask<ScreenshotClipboardReadResult> ReadClipboardImageAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _clipboardReader.ReadImageAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        EnsureOwnerThread();
        _disposed = true;
        UnregisterAllBestEffort();
        if (_subclassInstalled && _windowHandle != 0)
        {
            _ = NativeMethods.RemoveWindowSubclass(
                _windowHandle,
                _subclassProc,
                SubclassId);
        }

        _subclassInstalled = false;
        _windowHandle = 0;
        HotKeyPressed = null;
    }

    internal static uint GetNativeModifiers(ScreenshotHotKeyModifiers modifiers) =>
        checked((uint)modifiers | ModNoRepeat);

    internal static NativeMethods.Input[] CreateScreenshotShortcutInputs() =>
    [
        CreateKeyInput(VkLWin, keyUp: false),
        CreateKeyInput(VkShift, keyUp: false),
        CreateKeyInput(VkS, keyUp: false),
        CreateKeyInput(VkS, keyUp: true),
        CreateKeyInput(VkShift, keyUp: true),
        CreateKeyInput(VkLWin, keyUp: true),
    ];

    internal static NativeMethods.Input[] CreateScreenshotShortcutCleanupInputs() =>
    [
        CreateKeyInput(VkS, keyUp: true),
        CreateKeyInput(VkShift, keyUp: true),
        CreateKeyInput(VkLWin, keyUp: true),
    ];

    private nint WindowSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        _ = subclassId;
        _ = referenceData;
        if (message == WmHotKey && !_disposed)
        {
            int hotKeyId = unchecked((int)wParam);
            if (_registeredHotKeyIds.Contains(hotKeyId))
            {
                try
                {
                    HotKeyPressed?.Invoke(this, new ScreenshotHotKeyPressedEventArgs(hotKeyId));
                }
                catch
                {
                    // Exceptions must never cross a native window-procedure boundary.
                }
            }
        }
        else if (message == WmNcDestroy)
        {
            _disposed = true;
            UnregisterAllBestEffort();
            if (_subclassInstalled)
            {
                _ = NativeMethods.RemoveWindowSubclass(
                    windowHandle,
                    _subclassProc,
                    SubclassId);
            }

            _subclassInstalled = false;
            _windowHandle = 0;
            HotKeyPressed = null;
        }

        return NativeMethods.DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void UnregisterAllBestEffort()
    {
        if (_windowHandle == 0)
        {
            _registeredHotKeyIds.Clear();
            return;
        }

        foreach (int hotKeyId in _registeredHotKeyIds)
        {
            _ = NativeMethods.UnregisterHotKey(_windowHandle, hotKeyId);
        }

        _registeredHotKeyIds.Clear();
    }

    private void ThrowIfUnavailableOrWrongThread()
    {
        ObjectDisposedException.ThrowIf(_disposed || _windowHandle == 0, this);
        EnsureOwnerThread();
    }

    private void EnsureOwnerThread()
    {
        if (NativeMethods.GetCurrentThreadId() != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "Screenshot hotkey registration and teardown must run on the HWND owner thread.");
        }
    }

    private static void ValidateHotKeyId(int hotKeyId)
    {
        if (hotKeyId is < 0 or > 0xBFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(hotKeyId));
        }
    }

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static NativeMethods.Input CreateKeyInput(int virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                VirtualKey = checked((ushort)virtualKey),
                Flags = keyUp ? KeyEventFKeyUp : 0,
            },
        },
    };

    internal static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint SubclassProc(
            nint windowHandle,
            uint message,
            nuint wParam,
            nint lParam,
            nuint subclassId,
            nuint referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowSubclass(
            nint windowHandle,
            SubclassProc subclassProc,
            nuint subclassId,
            nuint referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RemoveWindowSubclass(
            nint windowHandle,
            SubclassProc subclassProc,
            nuint subclassId);

        [DllImport("comctl32.dll")]
        internal static extern nint DefSubclassProc(
            nint windowHandle,
            uint message,
            nuint wParam,
            nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(
            nint windowHandle,
            int hotKeyId,
            uint modifiers,
            uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(nint windowHandle, int hotKeyId);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint inputCount,
            [In] Input[] inputs,
            int inputSize);

        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int Dx;
            internal int Dy;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal nuint ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal nuint ExtraInfo;
        }
    }
}

internal sealed class UnavailableScreenshotCapturePlatform : IScreenshotCapturePlatform, IDisposable
{
    public event EventHandler<ScreenshotHotKeyPressedEventArgs>? HotKeyPressed
    {
        add { }
        remove { }
    }

    public ScreenshotHotKeyRegistrationStatus RegisterHotKey(int hotKeyId, ScreenshotHotKey hotKey) =>
        ScreenshotHotKeyRegistrationStatus.Failed;

    public bool UnregisterHotKey(int hotKeyId) => false;

    public bool AreCaptureKeysReleased(ScreenshotHotKey hotKey) => false;

    public bool SendScreenshotShortcut() => false;

    public ScreenshotForegroundWindowSnapshot GetForegroundWindowSnapshot() => default;

    public uint GetClipboardSequenceNumber() => 0;

    public ValueTask<ScreenshotClipboardAccessResult> ProbeClipboardAccessAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ScreenshotClipboardAccessResult.Unavailable);

    public ValueTask<ScreenshotClipboardReadResult> ReadClipboardImageAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ScreenshotClipboardReadResult.ClipboardUnavailable);

    public void Dispose()
    {
    }
}
