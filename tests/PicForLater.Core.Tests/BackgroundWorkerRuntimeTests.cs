using PicForLater.Core.Runtime;

namespace PicForLater.Core.Tests;

public sealed class BackgroundWorkerRuntimeTests
{
    private static readonly BackgroundWorkerSupervisorOptions ImmediateRetries = new(
        [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
        TimeSpan.FromMinutes(1));

    [Fact]
    public async Task TransientFailure_RestartsWorkerAndCancellationStopsCleanly()
    {
        using var cancellation = new CancellationTokenSource();
        var secondRunStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        var supervisor = CreateSupervisor(async token =>
        {
            if (Interlocked.Increment(ref runCount) == 1)
            {
                throw new TestTransientException("sensitive-path-and-key");
            }

            secondRunStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        Assert.True(supervisor.Start(cancellation.Token));
        await secondRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, runCount);
        Assert.Equal(BackgroundWorkerState.Stopped, supervisor.CurrentStatus.State);
        Assert.Null(supervisor.CurrentStatus.FailureCode);
    }

    [Fact]
    public async Task ReminderReconciliationTransientFailure_RestartsWithoutBecomingFaulted()
    {
        using var cancellation = new CancellationTokenSource();
        var reconciliationRecovered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        var supervisor = new BackgroundWorkerSupervisor(
            BackgroundWorkerKind.Reminders,
            async token =>
            {
                if (Interlocked.Increment(ref runCount) == 1)
                {
                    throw new TestTransientException("database path");
                }

                reconciliationRecovered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            exception => exception is TestTransientException
                ? new BackgroundWorkerFailure("background.reminders.storage-busy", true)
                : new BackgroundWorkerFailure("background.reminders.unexpected", false),
            "background.reminders.unexpected",
            ImmediateRetries);

        Assert.True(supervisor.Start(cancellation.Token));
        await reconciliationRecovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BackgroundWorkerState.Running, supervisor.CurrentStatus.State);
        await cancellation.CancelAsync();
        await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, runCount);
        Assert.Equal(BackgroundWorkerState.Stopped, supervisor.CurrentStatus.State);
    }

    [Fact]
    public async Task ConsecutiveTransientFailures_ReachFaultedAfterBoundedRetries()
    {
        var runCount = 0;
        var supervisor = CreateSupervisor(_ =>
        {
            Interlocked.Increment(ref runCount);
            return Task.FromException(new TestTransientException("secret"));
        });

        Assert.True(supervisor.Start(CancellationToken.None));
        await WaitForStateAsync(supervisor, BackgroundWorkerState.Faulted);

        Assert.Equal(4, runCount);
        Assert.Equal("background.analysis.storage-busy", supervisor.CurrentStatus.FailureCode);
        Assert.DoesNotContain("secret", supervisor.CurrentStatus.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFailure_FaultsWithoutRestarting()
    {
        var runCount = 0;
        var supervisor = CreateSupervisor(_ =>
        {
            Interlocked.Increment(ref runCount);
            return Task.FromException(new InvalidOperationException("prompt-response-secret"));
        });

        Assert.True(supervisor.Start(CancellationToken.None));
        await WaitForStateAsync(supervisor, BackgroundWorkerState.Faulted);

        Assert.Equal(1, runCount);
        Assert.Equal("background.analysis.unexpected", supervisor.CurrentStatus.FailureCode);
        Assert.DoesNotContain("prompt-response-secret", supervisor.CurrentStatus.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_AllowsOnlyOneReplacementWorker()
    {
        using var cancellation = new CancellationTokenSource();
        var replacementStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        var supervisor = CreateSupervisor(async token =>
        {
            if (Interlocked.Increment(ref runCount) == 1)
            {
                throw new InvalidOperationException();
            }

            replacementStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        Assert.True(supervisor.Start(cancellation.Token));
        await WaitForStateAsync(supervisor, BackgroundWorkerState.Faulted);
        Assert.True(supervisor.Retry());
        Assert.False(supervisor.Retry());
        await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, runCount);
        Assert.Equal(BackgroundWorkerState.Stopped, supervisor.CurrentStatus.State);
    }

    [Fact]
    public void FailureCircuit_OpensAtThresholdAndResetRequiresFaultedState()
    {
        var circuit = new BackgroundFailureCircuit(
            BackgroundWorkerKind.LocalInference,
            failureThreshold: 3,
            "background.local-worker.crash-loop");

        Assert.False(circuit.ReportFailure(10));
        Assert.False(circuit.ReportFailure(20));
        Assert.True(circuit.ReportFailure(30));
        Assert.True(circuit.IsOpen);
        Assert.Equal(3, circuit.CurrentStatus.RetryAttempt);
        Assert.Equal(30, circuit.CurrentStatus.NativeExitCode);
        Assert.True(circuit.Reset());
        Assert.False(circuit.IsOpen);
        Assert.Equal(BackgroundWorkerState.Starting, circuit.CurrentStatus.State);
        Assert.False(circuit.Reset());
        circuit.ReportSuccess();
        Assert.Equal(BackgroundWorkerState.Running, circuit.CurrentStatus.State);
    }

    [Fact]
    public void FailureCircuit_DropsFailuresOutsideTheConsecutiveWindow()
    {
        var time = new ManualTimeProvider();
        var circuit = new BackgroundFailureCircuit(
            BackgroundWorkerKind.LocalInference,
            failureThreshold: 3,
            "background.local-worker.crash-loop",
            resetFailuresAfter: TimeSpan.FromMinutes(2),
            timeProvider: time);

        Assert.False(circuit.ReportFailure());
        Assert.False(circuit.ReportFailure());
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(circuit.ReportFailure());
        Assert.Equal(1, circuit.CurrentStatus.RetryAttempt);
        circuit.LatchFailure(55);
        Assert.True(circuit.IsOpen);
        Assert.Equal(55, circuit.CurrentStatus.NativeExitCode);
    }

    private static BackgroundWorkerSupervisor CreateSupervisor(
        Func<CancellationToken, Task> worker) =>
        new(
            BackgroundWorkerKind.Analysis,
            worker,
            exception => exception is TestTransientException
                ? new BackgroundWorkerFailure("background.analysis.storage-busy", true)
                : new BackgroundWorkerFailure("background.analysis.unexpected", false),
            "background.analysis.unexpected",
            ImmediateRetries);

    private static async Task WaitForStateAsync(
        BackgroundWorkerSupervisor supervisor,
        BackgroundWorkerState state)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(2);
        while (supervisor.CurrentStatus.State != state && DateTime.UtcNow < timeoutAt)
        {
            await Task.Delay(10);
        }

        Assert.Equal(state, supervisor.CurrentStatus.State);
        await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class TestTransientException(string message) : Exception(message);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
    }
}
