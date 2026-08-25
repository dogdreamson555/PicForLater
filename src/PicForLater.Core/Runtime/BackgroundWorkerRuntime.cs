namespace PicForLater.Core.Runtime;

public enum BackgroundWorkerKind
{
    Analysis = 1,
    Reminders = 2,
    LocalInference = 3,
}

public enum BackgroundWorkerState
{
    Starting = 1,
    Running = 2,
    Retrying = 3,
    Faulted = 4,
    Stopped = 5,
}

public sealed record BackgroundWorkerStatus(
    BackgroundWorkerKind Kind,
    BackgroundWorkerState State,
    string? FailureCode,
    int RetryAttempt,
    DateTimeOffset? NextRetryAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int? NativeExitCode = null);

public sealed record BackgroundWorkerFailure(
    string ErrorCode,
    bool IsTransient);

public sealed record BackgroundWorkerSupervisorOptions(
    IReadOnlyList<TimeSpan> RetryDelays,
    TimeSpan ResetAttemptsAfter)
{
    public static BackgroundWorkerSupervisorOptions Default { get; } = new(
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)],
        TimeSpan.FromSeconds(30));
}

public sealed class BackgroundWorkerSupervisor
{
    private readonly object _syncRoot = new();
    private readonly BackgroundWorkerKind _kind;
    private readonly Func<CancellationToken, Task> _runWorker;
    private readonly Func<Exception, BackgroundWorkerFailure> _classifyFailure;
    private readonly string _unexpectedStopFailureCode;
    private readonly BackgroundWorkerSupervisorOptions _options;
    private readonly TimeProvider _timeProvider;
    private CancellationToken _lifetimeToken;
    private bool _hasLifetimeToken;
    private Task _completion = Task.CompletedTask;
    private BackgroundWorkerStatus _status;

    public BackgroundWorkerSupervisor(
        BackgroundWorkerKind kind,
        Func<CancellationToken, Task> runWorker,
        Func<Exception, BackgroundWorkerFailure> classifyFailure,
        string unexpectedStopFailureCode,
        BackgroundWorkerSupervisorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        _kind = kind;
        _runWorker = runWorker ?? throw new ArgumentNullException(nameof(runWorker));
        _classifyFailure = classifyFailure
            ?? throw new ArgumentNullException(nameof(classifyFailure));
        ArgumentException.ThrowIfNullOrWhiteSpace(unexpectedStopFailureCode);
        _unexpectedStopFailureCode = unexpectedStopFailureCode;
        _options = options ?? BackgroundWorkerSupervisorOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_options.RetryDelays is null
            || _options.RetryDelays.Count == 0
            || _options.RetryDelays.Any(delay => delay < TimeSpan.Zero)
            || _options.ResetAttemptsAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _status = CreateStatus(BackgroundWorkerState.Stopped);
    }

    public event Action<BackgroundWorkerStatus>? StatusChanged;

    public BackgroundWorkerStatus CurrentStatus
    {
        get
        {
            lock (_syncRoot)
            {
                return _status;
            }
        }
    }

    public Task Completion
    {
        get
        {
            lock (_syncRoot)
            {
                return _completion;
            }
        }
    }

    public bool Start(CancellationToken lifetimeToken)
    {
        BackgroundWorkerStatus status;
        var startGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_syncRoot)
        {
            if (_hasLifetimeToken || lifetimeToken.IsCancellationRequested)
            {
                return false;
            }

            _hasLifetimeToken = true;
            _lifetimeToken = lifetimeToken;
            status = SetStatusLocked(BackgroundWorkerState.Starting);
            _completion = Task.Run(async () =>
            {
                await startGate.Task.ConfigureAwait(false);
                await RunSupervisedAsync(lifetimeToken).ConfigureAwait(false);
            });
        }

        NotifyStatusChanged(status);
        startGate.SetResult();
        return true;
    }

    public bool Retry()
    {
        BackgroundWorkerStatus status;
        var startGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_syncRoot)
        {
            if (!_hasLifetimeToken
                || _lifetimeToken.IsCancellationRequested
                || _status.State != BackgroundWorkerState.Faulted)
            {
                return false;
            }

            status = SetStatusLocked(BackgroundWorkerState.Starting);
            _completion = Task.Run(async () =>
            {
                await startGate.Task.ConfigureAwait(false);
                await RunSupervisedAsync(_lifetimeToken).ConfigureAwait(false);
            });
        }

        NotifyStatusChanged(status);
        startGate.SetResult();
        return true;
    }

    private async Task RunSupervisedAsync(CancellationToken cancellationToken)
    {
        var retryAttempt = 0;
        while (true)
        {
            var startedAtUtc = _timeProvider.GetUtcNow();
            PublishStatus(BackgroundWorkerState.Running);
            try
            {
                await _runWorker(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    PublishStatus(BackgroundWorkerState.Stopped);
                }
                else
                {
                    PublishStatus(
                        BackgroundWorkerState.Faulted,
                        _unexpectedStopFailureCode);
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PublishStatus(BackgroundWorkerState.Stopped);
                return;
            }
            catch (Exception exception)
            {
                BackgroundWorkerFailure failure;
                try
                {
                    failure = _classifyFailure(exception);
                    ArgumentException.ThrowIfNullOrWhiteSpace(failure.ErrorCode);
                }
                catch
                {
                    failure = new BackgroundWorkerFailure(
                        _unexpectedStopFailureCode,
                        IsTransient: false);
                }

                if (!failure.IsTransient)
                {
                    PublishStatus(BackgroundWorkerState.Faulted, failure.ErrorCode);
                    return;
                }

                if (_timeProvider.GetUtcNow() - startedAtUtc >= _options.ResetAttemptsAfter)
                {
                    retryAttempt = 0;
                }

                retryAttempt++;
                if (retryAttempt > _options.RetryDelays.Count)
                {
                    PublishStatus(
                        BackgroundWorkerState.Faulted,
                        failure.ErrorCode,
                        retryAttempt);
                    return;
                }

                var delay = _options.RetryDelays[retryAttempt - 1];
                var nextRetryAtUtc = _timeProvider.GetUtcNow().Add(delay);
                PublishStatus(
                    BackgroundWorkerState.Retrying,
                    failure.ErrorCode,
                    retryAttempt,
                    nextRetryAtUtc);
                try
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    PublishStatus(BackgroundWorkerState.Stopped);
                    return;
                }
            }
        }
    }

    private void PublishStatus(
        BackgroundWorkerState state,
        string? failureCode = null,
        int retryAttempt = 0,
        DateTimeOffset? nextRetryAtUtc = null)
    {
        BackgroundWorkerStatus status;
        lock (_syncRoot)
        {
            status = SetStatusLocked(state, failureCode, retryAttempt, nextRetryAtUtc);
        }

        NotifyStatusChanged(status);
    }

    private BackgroundWorkerStatus SetStatusLocked(
        BackgroundWorkerState state,
        string? failureCode = null,
        int retryAttempt = 0,
        DateTimeOffset? nextRetryAtUtc = null)
    {
        _status = CreateStatus(state, failureCode, retryAttempt, nextRetryAtUtc);
        return _status;
    }

    private BackgroundWorkerStatus CreateStatus(
        BackgroundWorkerState state,
        string? failureCode = null,
        int retryAttempt = 0,
        DateTimeOffset? nextRetryAtUtc = null) =>
        new(
            _kind,
            state,
            failureCode,
            retryAttempt,
            nextRetryAtUtc,
            _timeProvider.GetUtcNow());

    private void NotifyStatusChanged(BackgroundWorkerStatus status)
    {
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<BackgroundWorkerStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(status);
            }
            catch
            {
                // A presentation or diagnostic subscriber must not stop supervision.
            }
        }
    }
}

public sealed class BackgroundFailureCircuit
{
    private readonly object _syncRoot = new();
    private readonly BackgroundWorkerKind _kind;
    private readonly int _failureThreshold;
    private readonly string _failureCode;
    private readonly TimeSpan _resetFailuresAfter;
    private readonly TimeProvider _timeProvider;
    private int _consecutiveFailures;
    private DateTimeOffset? _lastFailureAtUtc;
    private BackgroundWorkerStatus _status;

    public BackgroundFailureCircuit(
        BackgroundWorkerKind kind,
        int failureThreshold,
        string failureCode,
        TimeSpan? resetFailuresAfter = null,
        TimeProvider? timeProvider = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (failureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        _kind = kind;
        _failureThreshold = failureThreshold;
        _failureCode = failureCode;
        _resetFailuresAfter = resetFailuresAfter ?? TimeSpan.FromMinutes(2);
        if (_resetFailuresAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(resetFailuresAfter));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _status = CreateStatus(BackgroundWorkerState.Running);
    }

    public event Action<BackgroundWorkerStatus>? StatusChanged;

    public bool IsOpen
    {
        get
        {
            lock (_syncRoot)
            {
                return _status.State == BackgroundWorkerState.Faulted;
            }
        }
    }

    public BackgroundWorkerStatus CurrentStatus
    {
        get
        {
            lock (_syncRoot)
            {
                return _status;
            }
        }
    }

    public bool ReportFailure(int? nativeExitCode = null)
    {
        BackgroundWorkerStatus status;
        lock (_syncRoot)
        {
            if (_status.State == BackgroundWorkerState.Faulted)
            {
                return true;
            }

            var nowUtc = _timeProvider.GetUtcNow();
            if (_lastFailureAtUtc is { } lastFailureAtUtc
                && nowUtc - lastFailureAtUtc >= _resetFailuresAfter)
            {
                _consecutiveFailures = 0;
            }

            _consecutiveFailures++;
            _lastFailureAtUtc = nowUtc;
            status = CreateStatus(
                _consecutiveFailures >= _failureThreshold
                    ? BackgroundWorkerState.Faulted
                    : BackgroundWorkerState.Retrying,
                _failureCode,
                _consecutiveFailures,
                nativeExitCode);
            _status = status;
        }

        NotifyStatusChanged(status);
        return status.State == BackgroundWorkerState.Faulted;
    }

    public void LatchFailure(int? nativeExitCode = null)
    {
        BackgroundWorkerStatus status;
        lock (_syncRoot)
        {
            if (_status.State == BackgroundWorkerState.Faulted)
            {
                return;
            }

            _consecutiveFailures = _failureThreshold;
            _lastFailureAtUtc = _timeProvider.GetUtcNow();
            status = CreateStatus(
                BackgroundWorkerState.Faulted,
                _failureCode,
                _consecutiveFailures,
                nativeExitCode);
            _status = status;
        }

        NotifyStatusChanged(status);
    }

    public void ReportSuccess()
    {
        BackgroundWorkerStatus? status = null;
        lock (_syncRoot)
        {
            if (_consecutiveFailures == 0
                && _status.State == BackgroundWorkerState.Running)
            {
                return;
            }

            _consecutiveFailures = 0;
            _lastFailureAtUtc = null;
            status = CreateStatus(BackgroundWorkerState.Running);
            _status = status;
        }

        NotifyStatusChanged(status);
    }

    public bool Reset()
    {
        BackgroundWorkerStatus status;
        lock (_syncRoot)
        {
            if (_status.State != BackgroundWorkerState.Faulted)
            {
                return false;
            }

            _consecutiveFailures = 0;
            _lastFailureAtUtc = null;
            status = CreateStatus(BackgroundWorkerState.Starting);
            _status = status;
        }

        NotifyStatusChanged(status);
        return true;
    }

    public void Stop()
    {
        BackgroundWorkerStatus status;
        lock (_syncRoot)
        {
            status = CreateStatus(BackgroundWorkerState.Stopped);
            _status = status;
        }

        NotifyStatusChanged(status);
    }

    private BackgroundWorkerStatus CreateStatus(
        BackgroundWorkerState state,
        string? failureCode = null,
        int retryAttempt = 0,
        int? nativeExitCode = null) =>
        new(
            _kind,
            state,
            failureCode,
            retryAttempt,
            null,
            _timeProvider.GetUtcNow(),
            nativeExitCode);

    private void NotifyStatusChanged(BackgroundWorkerStatus status)
    {
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<BackgroundWorkerStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(status);
            }
            catch
            {
                // A presentation or diagnostic subscriber must not affect the circuit.
            }
        }
    }
}
