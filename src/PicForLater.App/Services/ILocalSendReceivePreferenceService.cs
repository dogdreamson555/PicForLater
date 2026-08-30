namespace PicForLater.App.Services;

public interface ILocalSendReceivePreferenceService
{
    bool IsEnabled { get; }

    void SetEnabled(bool isEnabled);
}
