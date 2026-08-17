using System.ComponentModel;
using System.Runtime.InteropServices;
using PicForLater.Infrastructure.Analysis;

namespace PicForLater.LocalInference;

/// <summary>
/// Makes an explicitly installed CUDA 12 / cuDNN 9 runtime visible to the
/// local-inference worker without changing the user or machine PATH.
/// </summary>
internal static class CudaRuntimeDependencyLoader
{
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;
    private const uint LoadLibrarySearchUserDirs = 0x00000400;
    private static readonly object SyncRoot = new();
    private static readonly List<nint> LoadedLibraryHandles = [];
    private static readonly List<nint> DllDirectoryCookies = [];
    private static readonly IReadOnlyList<string> DirectLoadFiles =
    [
        "cudart64_12.dll",
        "cublasLt64_12.dll",
        "cublas64_12.dll",
        "cufft64_11.dll",
    ];
    private static string? _managedRuntimeDirectoryPath;
    private static bool _isPrepared;

    public static void ConfigureManagedRuntimeDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Path.IsPathFullyQualified(directoryPath))
        {
            throw new ArgumentException(
                "The managed NVIDIA runtime directory must be absolute.",
                nameof(directoryPath));
        }

        lock (SyncRoot)
        {
            _managedRuntimeDirectoryPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(directoryPath));
        }
    }

    public static void Prepare()
    {
        lock (SyncRoot)
        {
            if (_isPrepared)
            {
                return;
            }

            if (_managedRuntimeDirectoryPath is null)
            {
                return;
            }

            var location = NvidiaCudaRuntimeLocator.Locate(_managedRuntimeDirectoryPath);
            if (location is null)
            {
                // Let ONNX Runtime produce its normal provider-load failure. A
                // later attempt can retry after the user installs the runtime.
                return;
            }

            if (!SetDefaultDllDirectories(
                    LoadLibrarySearchDefaultDirs | LoadLibrarySearchUserDirs))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not configure the native DLL search policy.");
            }

            AddSearchDirectory(location.CudaDirectoryPath);
            if (!location.CudnnDirectoryPath.Equals(
                    location.CudaDirectoryPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddSearchDirectory(location.CudnnDirectoryPath);
            }

            foreach (var fileName in DirectLoadFiles)
            {
                LoadedLibraryHandles.Add(
                    NativeLibrary.Load(Path.Combine(location.CudaDirectoryPath, fileName)));
            }

            LoadedLibraryHandles.Add(
                NativeLibrary.Load(Path.Combine(location.CudnnDirectoryPath, "cudnn64_9.dll")));
            _isPrepared = true;
        }
    }

    private static void AddSearchDirectory(string directory)
    {
        var cookie = AddDllDirectory(directory);
        if (cookie == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not register a native runtime directory.");
        }

        DllDirectoryCookies.Add(cookie);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AddDllDirectory(string newDirectory);
}
