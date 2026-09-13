using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PicForLater.App.Services;

/// <summary>
/// Observes Windows session-ending messages without blocking the native window
/// procedure. Normal user closes continue through AppWindow.Closing.
/// </summary>
internal sealed class WindowSessionMessageMonitor : IDisposable
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmNcDestroy = 0x0082;
    private static readonly nuint SubclassId = 0x50464C54;

    private readonly NativeMethods.SubclassProc _subclassProc;
    private nint _windowHandle;
    private bool _subclassInstalled;
    private bool _disposed;

    internal WindowSessionMessageMonitor(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException(
                "A valid main-window HWND is required.",
                nameof(windowHandle));
        }

        _windowHandle = windowHandle;
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

    internal event EventHandler? SessionEnding;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subclassInstalled && _windowHandle != 0)
        {
            _ = NativeMethods.RemoveWindowSubclass(
                _windowHandle,
                _subclassProc,
                SubclassId);
        }

        _subclassInstalled = false;
        _windowHandle = 0;
        SessionEnding = null;
    }

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
        _ = lParam;

        if (message == WmQueryEndSession && !_disposed)
        {
            // Do not wait for managed work here. DefSubclassProc preserves the
            // existing chain's response and returns promptly to Windows.
        }
        else if (message == WmEndSession && wParam != 0 && !_disposed)
        {
            try
            {
                SessionEnding?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // A native window procedure must never allow managed exceptions
                // to cross back into user32.
            }
        }
        else if (message == WmNcDestroy)
        {
            _disposed = true;
            if (_subclassInstalled)
            {
                _ = NativeMethods.RemoveWindowSubclass(
                    windowHandle,
                    _subclassProc,
                    SubclassId);
            }

            _subclassInstalled = false;
            _windowHandle = 0;
            SessionEnding = null;
        }

        return NativeMethods.DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private static class NativeMethods
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
    }
}
