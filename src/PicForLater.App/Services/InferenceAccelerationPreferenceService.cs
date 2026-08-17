using PicForLater.Core.Analysis;

namespace PicForLater.App.Services;

public sealed class InferenceAccelerationPreferenceService : IInferenceAccelerationPreferenceService
{
    private const string PreferenceKey = "Analysis.InferenceAcceleration";
    private readonly object _gate = new();
    private InferenceAccelerationMode _currentMode;
    private InferenceExecutionStatus? _lastExecutionStatus;

    private InferenceAccelerationPreferenceService()
    {
        _currentMode = ReadMode();
        if ((_currentMode == InferenceAccelerationMode.DirectMlGpu && !IsDirectMlAvailable)
            || (_currentMode == InferenceAccelerationMode.CudaGpu && !IsCudaAvailable))
        {
            _currentMode = InferenceAccelerationMode.Automatic;
        }
    }

    public static InferenceAccelerationPreferenceService Instance { get; } = new();

    public event EventHandler? StateChanged;

#if PICFORLATER_CUDA_RUNTIME
    public bool IsDirectMlAvailable => false;

    public bool IsCudaAvailable => true;
#else
    public bool IsDirectMlAvailable => true;

    public bool IsCudaAvailable => false;
#endif

    public InferenceAccelerationMode CurrentMode
    {
        get
        {
            lock (_gate)
            {
                return _currentMode;
            }
        }
    }

    public InferenceExecutionStatus? LastExecutionStatus
    {
        get
        {
            lock (_gate)
            {
                return _lastExecutionStatus;
            }
        }
    }

    public void SetMode(InferenceAccelerationMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if ((mode == InferenceAccelerationMode.DirectMlGpu && !IsDirectMlAvailable)
            || (mode == InferenceAccelerationMode.CudaGpu && !IsCudaAvailable))
        {
            throw new NotSupportedException("The selected inference runtime is not packaged for this architecture.");
        }

        LocalPreferenceStore.Instance.SetInt32(PreferenceKey, (int)mode);
        lock (_gate)
        {
            _currentMode = mode;
        }

        RaiseStateChanged();
    }

    public void ReportExecution(
        string workload,
        InferenceExecutionDevice device,
        bool usedAutomaticFallback = false,
        string? failureCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workload);
        if (!Enum.IsDefined(device))
        {
            throw new ArgumentOutOfRangeException(nameof(device));
        }

        lock (_gate)
        {
            _lastExecutionStatus = new InferenceExecutionStatus(
                workload,
                device,
                usedAutomaticFallback,
                failureCode,
                DateTimeOffset.UtcNow);
        }

        RaiseStateChanged();
    }

    private static InferenceAccelerationMode ReadMode()
    {
        if (LocalPreferenceStore.Instance.TryGetInt32(PreferenceKey, out var numeric)
            && Enum.IsDefined(typeof(InferenceAccelerationMode), numeric))
        {
            return (InferenceAccelerationMode)numeric;
        }

        return InferenceAccelerationMode.Automatic;
    }

    private void RaiseStateChanged()
    {
        var handlers = StateChanged;
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
            catch
            {
                // A settings page can be recreated during navigation. Runtime
                // selection must not fail because a stale observer disappeared.
            }
        }
    }
}
