using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Llm;

namespace Dsh.Gui.ViewModels;

/**
 * 底部状态行的统计: 轮/步、LLM 耗时、首 token 延迟、tok/s、缓存命中与累计 token。
 * The clock is injectable so elapsed-time rules can be checked without depending on how fast the machine runs.
 */
public sealed partial class TokenStatsViewModel(TimeProvider clock) : ObservableObject
{
    public const string Empty = "—";
    private const double MinimumMeasurableSeconds = 0.1;

    /** Start timestamp of the running request; null when no request is being timed. */
    private long? _requestStartedAt;
    /** Duration of the last finished request, reported while no request runs. */
    private TimeSpan _finishedElapsed;
    private bool _firstTokenSeen;
    private double _inputTokens;
    private double _outputTokens;
    private double _cacheReadTokens;

    [ObservableProperty]
    private string _rounds = Empty;

    [ObservableProperty]
    private string _latency = Empty;

    [ObservableProperty]
    private string _firstToken = Empty;

    [ObservableProperty]
    private string _tokensPerSecond = Empty;

    [ObservableProperty]
    private string _cache = Empty;

    [ObservableProperty]
    private string _totals = Empty;

    public TokenStatsViewModel()
        : this(TimeProvider.System)
    {
    }

    private TimeSpan RequestElapsed
        => _requestStartedAt is { } startedAt ? clock.GetElapsedTime(startedAt) : _finishedElapsed;

    public void Reset()
    {
        _inputTokens = 0;
        _outputTokens = 0;
        _cacheReadTokens = 0;
        _requestStartedAt = null;
        _finishedElapsed = TimeSpan.Zero;
        _firstTokenSeen = false;
        Rounds = Empty;
        Latency = Empty;
        FirstToken = Empty;
        TokensPerSecond = Empty;
        Cache = Empty;
        Totals = Empty;
    }

    public void ObserveTurn(int turn, int step)
        => Rounds = $"轮 {turn} · 步 {step}";

    public void RequestStarted()
    {
        _firstTokenSeen = false;
        _finishedElapsed = TimeSpan.Zero;
        _requestStartedAt = clock.GetTimestamp();
    }

    public void FirstTokenArrived()
    {
        if (_firstTokenSeen || _requestStartedAt is null)
            return;
        _firstTokenSeen = true;
        FirstToken = $"首 token {RequestElapsed.TotalSeconds:0.0}s";
    }

    public void RequestFinished()
    {
        if (_requestStartedAt is null)
            return;
        _finishedElapsed = RequestElapsed;
        _requestStartedAt = null;
        var seconds = _finishedElapsed.TotalSeconds;
        if (seconds >= MinimumMeasurableSeconds)
            Latency = $"LLM {seconds:0.0}s";
    }

    public void ObserveUsage(TokenUsage usage)
    {
        var total = usage.InputTokens + usage.OutputTokens;
        if (total <= 0)
            return;
        _inputTokens += usage.InputTokens;
        _outputTokens += usage.OutputTokens;
        _cacheReadTokens += usage.CacheReadTokens ?? 0;
        Totals = $"in {Compact(_inputTokens)} / out {Compact(_outputTokens)}";
        Cache = _inputTokens > 0
            ? $"缓存命中 {_cacheReadTokens / _inputTokens * 100:0}%"
            : Empty;
        var seconds = RequestElapsed.TotalSeconds;
        // 重放历史会话时事件是瞬间连发的, 这种耗时算出来的 tok/s 没有意义。
        if (usage.OutputTokens > 0 && seconds >= MinimumMeasurableSeconds)
            TokensPerSecond = $"{usage.OutputTokens / seconds:0.0} tok/s";
    }

    private static string Compact(double value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000:0.0}M",
        >= 1_000 => $"{value / 1_000:0.0}k",
        _ => value.ToString("0"),
    };
}
