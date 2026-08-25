using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.Core.Runtime;

namespace PicForLater.App.ViewModels;

public partial class BackgroundWorkersStatusViewModel : ObservableObject
{
    private static readonly ResourceLoader Resources = new();
    private readonly Func<Task> _retryFaultedWorkers;

    public BackgroundWorkersStatusViewModel(Func<Task> retryFaultedWorkers)
    {
        _retryFaultedWorkers = retryFaultedWorkers
            ?? throw new ArgumentNullException(nameof(retryFaultedWorkers));
    }

    [ObservableProperty]
    public partial bool HasFault { get; set; }

    [ObservableProperty]
    public partial string FaultMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRetrying { get; set; }

    public void Update(IReadOnlyList<BackgroundWorkerStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var faultedKinds = statuses
            .Where(status => status.State == BackgroundWorkerState.Faulted)
            .Select(status => status.Kind)
            .Distinct()
            .ToHashSet();
        var message = faultedKinds.Count switch
        {
            0 => string.Empty,
            1 when faultedKinds.Contains(BackgroundWorkerKind.Analysis) =>
                Resources.GetString("BackgroundAnalysisStoppedMessage"),
            1 when faultedKinds.Contains(BackgroundWorkerKind.Reminders) =>
                Resources.GetString("BackgroundRemindersStoppedMessage"),
            1 when faultedKinds.Contains(BackgroundWorkerKind.LocalInference) =>
                Resources.GetString("BackgroundLocalInferenceStoppedMessage"),
            _ => Resources.GetString("BackgroundWorkersStoppedMessage"),
        };
        FaultMessage = message;
        HasFault = faultedKinds.Count > 0;
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (!HasFault || IsRetrying)
        {
            return;
        }

        IsRetrying = true;
        try
        {
            await _retryFaultedWorkers().ConfigureAwait(true);
        }
        finally
        {
            IsRetrying = false;
        }
    }
}
