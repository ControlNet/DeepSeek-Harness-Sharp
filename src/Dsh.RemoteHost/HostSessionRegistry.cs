using System.Text.Json;
using System.Threading.Channels;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/**
 * 统一会话登记表: 只登记本 host 创建或恢复的会话(harness / external / acp / terminal), 持久化成 JSON, host 重启后恢复列表。
 * 记录里保存的是进程/Agent 的基础状态(Running / Idle / Exited); 对外的状态再叠加待决策数量: 有未决请求即 WaitingForUser。
 * 每次对外可见的变化都经 host.sessions.changed 推给所有连接; 只有活动时间变化时按 ActivityPushInterval 合并推送。
 */
public sealed class HostSessionRegistry : IAsyncDisposable
{
    /** 只有 lastActivityAt 变化时两次推送的最小间隔(毫秒): 终端输出与流式事件很密, 逐条推送会淹没客户端。 */
    private const long ActivityPushIntervalMilliseconds = 2000;

    private readonly Lock _gate = new();
    private readonly HostBroadcast _broadcast = new();
    private readonly OrderedDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HostPendingDecisions _pending;
    private readonly string _storePath;
    private readonly Channel<bool> _saveRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _saver;

    /** Pushed 是上一次推送出去的对外投影: 待决策数在登记表之外变化, 只有和它比较才能发现变化。 */
    private sealed class Entry(RemoteHostSession record)
    {
        public RemoteHostSession Record { get; set; } = record;

        public RemoteHostSession Pushed { get; set; } = record;
    }

    private HostSessionRegistry(string storePath, HostPendingDecisions pending, IEnumerable<RemoteHostSession> records)
    {
        _storePath = storePath;
        _pending = pending;
        foreach (var record in records)
            _entries[record.SessionId] = new Entry(record);
        pending.CountChanged += OnPendingChanged;
        _saver = Task.Run(SaveLoopAsync);
    }

    /** 读出上次的登记表; 上一个 host 进程托管的会话已随它结束, 一律记为 Exited(仍活着的终端由 HostPtySessions 对账后复活)。 */
    public static async Task<HostSessionRegistry> OpenAsync(string storePath, HostPendingDecisions pending, CancellationToken cancellationToken)
    {
        var records = await LoadAsync(storePath, cancellationToken).ConfigureAwait(false);
        return new HostSessionRegistry(storePath, pending, records.Select(record => record with
        {
            Status = HostSessionStatuses.Exited,
            PendingCount = 0,
        }));
    }

    public IReadOnlyList<RemoteHostSession> List()
    {
        lock (_gate)
            return [.. _entries.Values.Select(Project)];
    }

    public RemoteHostSession? Get(string sessionId)
    {
        lock (_gate)
            return _entries.TryGetValue(sessionId, out var entry) ? Project(entry) : null;
    }

    /** 登记或重新登记(恢复会话): 已有记录保留其 CreatedAt, 其余字段取新值。 */
    public void Register(RemoteHostSession record)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(record.SessionId, out var existing))
            {
                existing.Record = record with { CreatedAt = existing.Record.CreatedAt, PendingCount = 0 };
                PublishIfChanged(existing);
                return;
            }

            var entry = new Entry(record with { PendingCount = 0 });
            _entries[record.SessionId] = entry;
            Publish(entry);
        }
    }

    /** 基础状态(Running / Idle); 离开 Exited 时清掉退出码。 */
    public void SetStatus(string sessionId, string status)
        => Update(sessionId, record => record with { Status = status, ExitCode = status == HostSessionStatuses.Exited ? record.ExitCode : null });

    public void SetTitle(string sessionId, string? title) => Update(sessionId, record => record with { Title = title });

    public void Touch(string sessionId, long time)
        => Update(sessionId, record => time > record.LastActivityAt ? record with { LastActivityAt = time } : record);

    /** 会话进程结束: 记退出码, 并取消它所有还在等人回答的请求。 */
    public void MarkExited(string sessionId, int? exitCode)
    {
        Update(sessionId, record => record with { Status = HostSessionStatuses.Exited, ExitCode = exitCode });
        _pending.CancelSession(sessionId);
    }

    /** 每个连接一份: 只推实时变化, 完整列表由 host.sessions.list 查询。 */
    public HostSubscription Subscribe() => _broadcast.Subscribe([]);

    public async ValueTask DisposeAsync()
    {
        _pending.CountChanged -= OnPendingChanged;
        _saveRequests.Writer.TryComplete();
        await _saver.ConfigureAwait(false);
        await SaveAsync().ConfigureAwait(false);
    }

    private void OnPendingChanged(string sessionId) => Update(sessionId, record => record);

    private void Update(string sessionId, Func<RemoteHostSession, RemoteHostSession> change)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var entry))
                return;
            entry.Record = change(entry.Record);
            PublishIfChanged(entry);
        }
    }

    private void PublishIfChanged(Entry entry)
    {
        var after = Project(entry);
        var before = entry.Pushed;
        if (after == before)
            return;
        var activityOnly = after == before with { LastActivityAt = after.LastActivityAt };
        if (activityOnly && after.LastActivityAt - before.LastActivityAt < ActivityPushIntervalMilliseconds)
            return;
        Publish(entry);
    }

    private void Publish(Entry entry)
    {
        var projected = Project(entry);
        entry.Pushed = projected;
        _broadcast.Publish(HostNotification.Create(HostProtocol.NotificationSessionsChanged, projected, HostProtocolJsonContext.Default.RemoteHostSession));
        _saveRequests.Writer.TryWrite(true);
    }

    /** 对外状态: Exited 优先; 否则有未决请求即 WaitingForUser; 否则基础状态。 */
    private RemoteHostSession Project(Entry entry)
    {
        var record = entry.Record;
        var pending = _pending.CountFor(record.SessionId);
        var status = record.Status != HostSessionStatuses.Exited && pending > 0 ? HostSessionStatuses.WaitingForUser : record.Status;
        return record with { Status = status, PendingCount = pending };
    }

    private async Task SaveLoopAsync()
    {
        await foreach (var _ in _saveRequests.Reader.ReadAllAsync().ConfigureAwait(false))
            await SaveAsync().ConfigureAwait(false);
    }

    /** 写临时文件再整体替换: host 被杀时不会留下半截 JSON。 */
    private async Task SaveAsync()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(List(), HostProtocolJsonContext.Default.IReadOnlyListRemoteHostSession);
        OwnerOnlyFiles.EnsureDirectory(Path.GetDirectoryName(_storePath)!);
        var temporary = $"{_storePath}.tmp";
        await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
        File.Move(temporary, _storePath, overwrite: true);
    }

    private static async Task<IReadOnlyList<RemoteHostSession>> LoadAsync(string storePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
            return [];
        try
        {
            await using var stream = File.OpenRead(storePath);
            return await JsonSerializer.DeserializeAsync(stream, HostProtocolJsonContext.Default.IReadOnlyListRemoteHostSession, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException)
        {
            // 损坏的登记表不阻止 host 启动: 从空表开始, 下一次保存覆盖它。
            return [];
        }
    }
}
