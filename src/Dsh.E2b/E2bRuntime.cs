using System.Diagnostics;
using Dsh.Runtime;

namespace Dsh.E2b;

/** 沙盒运行配置:apiKey 可以内联,也可以指向环境变量(默认 E2B_API_KEY)。 */
public sealed class E2bRuntimeConfig
{
    public const string DefaultBaseUrl = "https://api.e2b.dev";
    public const string DefaultTemplate = "base";
    public const string DefaultApiKeyEnv = "E2B_API_KEY";
    public const long DefaultTimeoutMs = 120_000;

    public string? ApiKey { get; init; }

    public string ApiKeyEnv { get; init; } = DefaultApiKeyEnv;

    public string BaseUrl { get; init; } = DefaultBaseUrl;

    public string Template { get; init; } = DefaultTemplate;

    public long TimeoutMs { get; init; } = DefaultTimeoutMs;

    /** 内联 key 优先,否则读环境变量。 */
    public string? ResolveApiKey()
        => string.IsNullOrWhiteSpace(ApiKey) ? Environment.GetEnvironmentVariable(ApiKeyEnv) : ApiKey;
}

public sealed record E2bRunResult(string SandboxId, string Stdout, string Stderr, int ExitCode, long DurationMs);

/** E2B 沙盒服务:一次调用 = 新建沙盒 → 执行命令 → 销毁,不跨调用保留状态。 */
public sealed class E2bRuntime : Service
{
    public const string ServiceName = "e2b";

    private readonly E2bClient _client;
    private readonly E2bRuntimeConfig _config;

    public E2bRuntime(Context ctx, E2bRuntimeConfig config, HttpClient? http = null) : base(ctx, ServiceName)
    {
        _config = config;
        var client = http ?? new HttpClient(OutboundProxy.FromContext(ctx).CreateHandler());
        if (!string.IsNullOrWhiteSpace(config.ResolveApiKey()))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-API-KEY", config.ResolveApiKey());
        _client = new E2bClient(client, config.BaseUrl);
    }

    public async Task<E2bRunResult> RunAsync(
        string command,
        string? template = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var sandboxId = await _client.CreateSandboxAsync(template ?? _config.Template, cancellationToken);
        try
        {
            var result = await _client.ExecuteCommandAsync(sandboxId, command, cancellationToken);
            return new E2bRunResult(
                sandboxId, result.Stdout, result.Stderr, result.ExitCode,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            try
            {
                await _client.KillSandboxAsync(sandboxId, cancellationToken);
            }
            catch (Exception error)
            {
                Ctx.Logger.Warn("%s", $"e2b sandbox \"{sandboxId}\" could not be killed: {error.Message}");
            }
        }
    }
}
