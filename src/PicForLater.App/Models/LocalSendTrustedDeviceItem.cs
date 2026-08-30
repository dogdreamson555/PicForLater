namespace PicForLater.App.Models;

public sealed record LocalSendTrustedDeviceItem(
    string DeviceId,
    string DisplayName,
    string PairedDescription,
    string RemoveAutomationName)
{
    public string RemoveAutomationId => $"LocalSendTrustedDeviceRemove_{DeviceId}";
}
