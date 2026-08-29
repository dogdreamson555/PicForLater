#if PICFORLATER_UI_TESTING
using PicForLater.Infrastructure.LocalSend;

namespace PicForLater.App.Services;

internal sealed class UiTestLocalSendReceiverService : ILocalSendReceiverService
{
    private static readonly TimeSpan PairingDuration = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset FixturePairedAtUtc =
        new(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);

    private LocalSendReceiverSnapshot _snapshot = new(LocalSendReceiverStatus.Disabled);
    private CancellationTokenSource? _pairingTimeout;
    private Task _pairingTimeoutTask = Task.CompletedTask;
    private bool _disposed;

#if PICFORLATER_UI_VISUAL_FIXTURE
    private readonly List<LocalSendTrustedDeviceSummary> _trustedDevices =
    [
        new(
            "11111111111111111111111111111111",
            "Test phone",
            FixturePairedAtUtc,
            FixturePairedAtUtc.AddHours(1)),
    ];
#else
    private readonly List<LocalSendTrustedDeviceSummary> _trustedDevices = [];
#endif

    public LocalSendReceiverSnapshot Snapshot => _snapshot;

    public event Action<LocalSendReceiverSnapshot>? SnapshotChanged;

    public event Action<LocalSendReceiveSummary>? TransferCompleted
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        SetSnapshot(new(LocalSendReceiverStatus.Listening));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await CancelPairingTimeoutAsync().ConfigureAwait(false);
        SetSnapshot(new(LocalSendReceiverStatus.Disabled));
    }

    public async Task<LocalSendPairingSession> BeginPairingAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_snapshot.Status != LocalSendReceiverStatus.Listening)
        {
            throw new InvalidOperationException("The fake receiver is not ready to pair.");
        }

        await CancelPairingTimeoutAsync().ConfigureAwait(false);
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(PairingDuration);
        const string pin = "123456";
        SetSnapshot(new(
            LocalSendReceiverStatus.Pairing,
            PairingPin: pin,
            PairingExpiresAtUtc: expiresAtUtc));
        _pairingTimeout = new CancellationTokenSource();
        _pairingTimeoutTask = ExpirePairingAsync(expiresAtUtc, _pairingTimeout.Token);
        return new LocalSendPairingSession(pin, expiresAtUtc);
    }

    public async Task CancelPairingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_snapshot.Status == LocalSendReceiverStatus.Pairing)
        {
            await CancelPairingTimeoutAsync().ConfigureAwait(false);
            SetSnapshot(new(LocalSendReceiverStatus.Listening));
        }
    }

    public Task<IReadOnlyList<LocalSendTrustedDeviceSummary>> GetTrustedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<LocalSendTrustedDeviceSummary>>(
            _trustedDevices.ToArray());
    }

    public Task<bool> RemoveTrustedDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var removed = _trustedDevices.RemoveAll(device =>
            StringComparer.Ordinal.Equals(device.DeviceId, deviceId)) > 0;
        return Task.FromResult(removed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CancelPairingTimeoutAsync().ConfigureAwait(false);
        _snapshot = new(LocalSendReceiverStatus.Disabled);
        SnapshotChanged = null;
    }

    private void SetSnapshot(LocalSendReceiverSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    private async Task ExpirePairingAsync(
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PairingDuration, cancellationToken).ConfigureAwait(false);
            if (!_disposed
                && _snapshot.Status == LocalSendReceiverStatus.Pairing
                && _snapshot.PairingExpiresAtUtc == expiresAtUtc)
            {
                SetSnapshot(new(LocalSendReceiverStatus.Listening));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CancelPairingTimeoutAsync()
    {
        var timeout = _pairingTimeout;
        var timeoutTask = _pairingTimeoutTask;
        _pairingTimeout = null;
        _pairingTimeoutTask = Task.CompletedTask;
        if (timeout is null)
        {
            return;
        }

        timeout.Cancel();
        await timeoutTask.ConfigureAwait(false);
        timeout.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
#endif
