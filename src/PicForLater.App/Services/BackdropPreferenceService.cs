using System.Diagnostics;
using PicForLater.App.Models;

namespace PicForLater.App.Services;

/// <summary>
/// Persists the selected Mica variant and notifies the main window to apply it.
/// Unsupported Windows versions fall back through the system backdrop APIs.
/// </summary>
public sealed class BackdropPreferenceService : IBackdropPreferenceService
{
    internal const string PreferenceKey = "Appearance.Backdrop";
    private static readonly AppBackdropPreference DefaultPreference =
        AppBackdropPreference.MicaAlt;

    private readonly IInt32PreferenceStore _store;

    private BackdropPreferenceService()
        : this(LocalPreferenceStore.Instance)
    {
    }

    internal BackdropPreferenceService(IInt32PreferenceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        CurrentPreference = ReadPreference();
    }

    public static BackdropPreferenceService Instance { get; } = new();

    public AppBackdropPreference CurrentPreference { get; private set; }

    public event EventHandler? PreferenceChanged;

    public void SetPreference(AppBackdropPreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        if (preference == CurrentPreference)
        {
            return;
        }

        // Update the in-memory value only after the durable write succeeds.
        _store.SetInt32(PreferenceKey, (int)preference);
        CurrentPreference = preference;
        NotifyPreferenceChanged();
    }

    private void NotifyPreferenceChanged()
    {
        var handlers = PreferenceChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                // Preference persistence has already succeeded. A failing UI
                // subscriber must not make the settings binding report failure
                // or prevent other subscribers from applying the new value.
                Trace.WriteLine(
                    $"Backdrop preference change notification failed: " +
                    $"{exception.GetType().Name}.");
            }
        }
    }

    private AppBackdropPreference ReadPreference()
    {
        if (_store.TryGetInt32(PreferenceKey, out var numericValue)
            && Enum.IsDefined(typeof(AppBackdropPreference), numericValue))
        {
            return (AppBackdropPreference)numericValue;
        }

        return DefaultPreference;
    }
}
