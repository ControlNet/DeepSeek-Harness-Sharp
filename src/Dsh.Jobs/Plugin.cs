using Dsh.Runtime;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Jobs.Plugin.JobsLocal)]
[assembly: DshPlugin(Dsh.Jobs.Plugin.ToolJobs)]

namespace Dsh.Jobs;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string JobsLocal = "@deepseek-ai/dsh-jobs-local";
    internal const string ToolJobs = "@deepseek-ai/dsh-tool-jobs";

    public string[] Inject => packageName switch
    {
        JobsLocal => [],
        ToolJobs => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, JobsService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        JobsLocal => RegisterJobsLocal(ctx, config),
        ToolJobs => global::Dsh.Jobs.ToolJobs.Register(ctx, ToolJobsConfigFrom(config)),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterJobsLocal(Context ctx, object? config)
    {
        _ = new LocalJobsService(ctx, new LocalJobsConfig
        {
            MaxConcurrentJobsPerOwner = IntOf(ConfigOf(config), "maxConcurrentJobsPerOwner") ?? LocalJobsConfig.DefaultMaxConcurrentJobsPerOwner,
        });
        return new NoopDisposable();
    }

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    private static ToolJobsConfig ToolJobsConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new ToolJobsConfig
        {
            WaitTimeoutMs = LongOf(dict, "waitTimeoutMs") ?? new ToolJobsConfig().WaitTimeoutMs,
            MaxWaitTimeoutMs = LongOf(dict, "maxWaitTimeoutMs") ?? new ToolJobsConfig().MaxWaitTimeoutMs,
            CompletionDelivery = dict?.GetValueOrDefault("completionDelivery") as string == "quiet"
                ? CompletionDelivery.Quiet : new ToolJobsConfig().CompletionDelivery,
            MaxConsecutiveWakes = IntOf(dict, "maxConsecutiveWakes") ?? new ToolJobsConfig().MaxConsecutiveWakes,
        };
    }

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => (int)value,
            int value => value,
            _ => null,
        };

    private static long? LongOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => value,
            int value => value,
            _ => null,
        };

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
