namespace PicForLater.App.Services;

internal sealed class CloseBehaviorPreferenceService
{
    internal const string KeepInSystemTrayPreferenceKey =
        "Window.KeepInSystemTray";

    private CloseBehaviorPreferenceService()
    {
        IsKeepInSystemTrayEnabled = LocalPreferenceStore.Instance.TryGetInt32(
            KeepInSystemTrayPreferenceKey,
            out var value) && value == 1;
    }

    internal static CloseBehaviorPreferenceService Instance { get; } = new();

    internal bool IsKeepInSystemTrayEnabled { get; private set; }

    internal void SetKeepInSystemTrayEnabled(bool isEnabled)
    {
        LocalPreferenceStore.Instance.SetInt32(
            KeepInSystemTrayPreferenceKey,
            isEnabled ? 1 : 0);
        IsKeepInSystemTrayEnabled = isEnabled;
    }
}
