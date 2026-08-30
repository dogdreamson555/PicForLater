namespace PicForLater.App.Services;

public sealed class LocalSendReceivePreferenceService : ILocalSendReceivePreferenceService
{
    private const string PreferenceKey = "LocalSend.ReceiveEnabled";

    private LocalSendReceivePreferenceService()
    {
        IsEnabled = LocalPreferenceStore.Instance.TryGetInt32(PreferenceKey, out var value)
                    && value == 1;
    }

    public static LocalSendReceivePreferenceService Instance { get; } = new();

    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool isEnabled)
    {
        LocalPreferenceStore.Instance.SetInt32(PreferenceKey, isEnabled ? 1 : 0);
        IsEnabled = isEnabled;
    }
}
