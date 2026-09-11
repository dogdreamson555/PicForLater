namespace PicForLater.App.Services;

internal interface IInt32PreferenceStore
{
    bool TryGetInt32(string key, out int value);

    void SetInt32(string key, int value);
}
