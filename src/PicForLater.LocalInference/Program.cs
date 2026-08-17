using System.IO.Pipes;

namespace PicForLater.LocalInference;

internal static class Program
{
    [MTAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = WorkerCommandLine.Parse(args);
            using var pipe = new NamedPipeClientStream(
                ".",
                options.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            await using var host = new LocalInferenceWorkerHost(pipe, options.ParentProcessId);
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}

internal sealed record WorkerCommandLine(string PipeName, int ParentProcessId)
{
    public static WorkerCommandLine Parse(IReadOnlyList<string> args)
    {
        string? pipeName = null;
        int? parentProcessId = null;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--pipe" when index + 1 < args.Count:
                    pipeName = args[++index];
                    break;
                case "--parent-pid" when index + 1 < args.Count
                    && int.TryParse(args[++index], out var parsed)
                    && parsed > 0:
                    parentProcessId = parsed;
                    break;
                default:
                    throw new ArgumentException("The local inference worker arguments are invalid.");
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName)
            || pipeName.Length > 180
            || !pipeName.StartsWith("PicForLater.LocalInference.", StringComparison.Ordinal)
            || parentProcessId is null)
        {
            throw new ArgumentException("The local inference worker arguments are invalid.");
        }

        return new WorkerCommandLine(pipeName, parentProcessId.Value);
    }
}
