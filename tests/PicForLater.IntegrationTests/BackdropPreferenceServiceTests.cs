using PicForLater.App.Models;
using PicForLater.App.Services;

namespace PicForLater.IntegrationTests;

public sealed class BackdropPreferenceServiceTests
{
    [Fact]
    public void MissingOrInvalidValueFallsBackToMicaAlt()
    {
        var missing = new BackdropPreferenceService(new MemoryInt32Store());
        var invalidStore = new MemoryInt32Store();
        invalidStore.Values[BackdropPreferenceService.PreferenceKey] = 99;
        var invalid = new BackdropPreferenceService(invalidStore);

        Assert.Equal(AppBackdropPreference.MicaAlt, missing.CurrentPreference);
        Assert.Equal(AppBackdropPreference.MicaAlt, invalid.CurrentPreference);
    }

    [Theory]
    [InlineData(AppBackdropPreference.Mica)]
    [InlineData(AppBackdropPreference.MicaAlt)]
    public void SetPreference_PersistsAndRaisesChangeNotification(
        AppBackdropPreference preference)
    {
        var store = new MemoryInt32Store();
        store.Values[BackdropPreferenceService.PreferenceKey] =
            (int)(preference == AppBackdropPreference.Mica
                ? AppBackdropPreference.MicaAlt
                : AppBackdropPreference.Mica);
        var service = new BackdropPreferenceService(store);
        var changeCount = 0;
        service.PreferenceChanged += (_, _) => changeCount++;

        service.SetPreference(preference);

        Assert.Equal((int)preference, store.Values[BackdropPreferenceService.PreferenceKey]);
        Assert.Equal(preference, service.CurrentPreference);
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void SetPreference_SameValueDoesNotWriteOrNotify()
    {
        var store = new MemoryInt32Store();
        var service = new BackdropPreferenceService(store);
        var changeCount = 0;
        service.PreferenceChanged += (_, _) => changeCount++;

        service.SetPreference(AppBackdropPreference.MicaAlt);

        Assert.Equal(0, store.SetCalls);
        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void SetPreference_WriteFailureKeepsPreviousValue()
    {
        var store = new MemoryInt32Store { ThrowOnSet = true };
        var service = new BackdropPreferenceService(store);

        Assert.Throws<IOException>(
            () => service.SetPreference(AppBackdropPreference.Mica));
        Assert.Equal(AppBackdropPreference.MicaAlt, service.CurrentPreference);
    }

    [Fact]
    public void SetPreference_IsolatesFailingChangeSubscribers()
    {
        var store = new MemoryInt32Store();
        var service = new BackdropPreferenceService(store);
        var notificationCount = 0;
        service.PreferenceChanged += (_, _) =>
            throw new InvalidOperationException("Test subscriber failure.");
        service.PreferenceChanged += (_, _) => notificationCount++;

        service.SetPreference(AppBackdropPreference.Mica);

        Assert.Equal(AppBackdropPreference.Mica, service.CurrentPreference);
        Assert.Equal(
            (int)AppBackdropPreference.Mica,
            store.Values[BackdropPreferenceService.PreferenceKey]);
        Assert.Equal(1, notificationCount);
    }

    [Theory]
    [InlineData(AppBackdropPreference.Mica, 2u)]
    [InlineData(AppBackdropPreference.MicaAlt, 4u)]
    public void ToDwmSystemBackdropType_UsesExpectedNativeMaterial(
        AppBackdropPreference preference,
        uint expectedType)
    {
        Assert.Equal(
            expectedType,
            BackdropPreferenceMapping.ToDwmSystemBackdropType(preference));
    }

    private sealed class MemoryInt32Store : IInt32PreferenceStore
    {
        internal Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);

        internal bool ThrowOnSet { get; init; }

        internal int SetCalls { get; private set; }

        public bool TryGetInt32(string key, out int value) => Values.TryGetValue(key, out value);

        public void SetInt32(string key, int value)
        {
            SetCalls++;
            if (ThrowOnSet)
            {
                throw new IOException("Test storage failure.");
            }

            Values[key] = value;
        }
    }
}
