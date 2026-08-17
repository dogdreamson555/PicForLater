using PicForLater.Infrastructure.Storage;

namespace PicForLater.App.Services;

internal static class AppRuntimePaths
{
    public static AppDataPaths Paths { get; } = CreatePaths();

    public static string UserDataRootPath => Paths.RootPath;

    public static string SettingsFilePath => Paths.SettingsFilePath;

    private static AppDataPaths CreatePaths()
    {
#if PICFORLATER_UI_TESTING
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.UiTests",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
#else
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localAppData)
            || !Path.IsPathFullyQualified(localAppData))
        {
            throw new InvalidOperationException("The current user LocalAppData path is unavailable.");
        }

        var rootPath = Path.Combine(Path.GetFullPath(localAppData), "PicForLater");
#endif
        return new AppDataPaths(rootPath);
    }
}
