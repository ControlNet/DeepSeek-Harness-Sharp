using Dsh.Runtime;
using Dsh.Llm;

namespace Dsh.Core;

public sealed class SessionStore(Context ctx) : Service(ctx, ServiceName)
{
    public const string ServiceName = "sessions";

    private sealed class Entry
    {
        public required Session Session { get; init; }
        public required IDisposable Detach { get; init; }
        public bool Announced;
    }

    private readonly Dictionary<SessionId, Entry> _sessions = [];

    public Session Create(SessionId? id = null, IReadOnlyList<SessionEvent>? seed = null, SessionHeader? header = null, long? inheritedEventCount = null)
    {
        var session = Session.Create(id ?? SessionId.Create(Guid.NewGuid().ToString()), seed, header, inheritedEventCount);
        Enter(session, Ctx);
        Announce(session);
        return session;
    }

    public Session Prepare(SessionId? id = null, IReadOnlyList<SessionEvent>? seed = null, SessionHeader? header = null, long? inheritedEventCount = null)
        => Session.Create(id ?? SessionId.Create(Guid.NewGuid().ToString()), seed, header, inheritedEventCount);

    public IDisposable Enter(Session session, Context owner)
    {
        if (_sessions.ContainsKey(session.Id))
            throw new InvalidOperationException($"session \"{session.Id}\" is already live in the store");
        Action<Session, SessionEvent> forward = (source, sessionEvent) => PublishEvent(source, sessionEvent);
        Action<Session, long> truncated = (source, eventCount) =>
            Ctx.Events.Emit(Ctx, new SessionTruncateNotification(source, eventCount));
        var detach = new SessionDetach(() =>
        {
            session.Appended -= forward;
            session.Truncated -= truncated;
            Detach(session);
        });
        _sessions[session.Id] = new Entry { Session = session, Detach = detach };
        session.Appended += forward;
        session.Truncated += truncated;
        return detach;
    }

    /**
     * 会话的持有者(agent)结束时释放:连同 ParentSession 指向它的子孙会话一起解绑并发出 SessionDisposedNotification,
     * 持久化据此关闭写句柄。子会话在父会话存续期间留在 store,供子代理树查询。
     */
    public void Release(SessionId id)
    {
        if (!_sessions.TryGetValue(id, out var entry))
            return;
        var children = _sessions.Values
            .Where(child => child.Session.Header.ParentSession == id)
            .Select(child => child.Session.Id)
            .ToList();
        entry.Detach.Dispose();
        foreach (var child in children)
            Release(child);
    }

    public void Announce(Session session)
    {
        if (!_sessions.TryGetValue(session.Id, out var entry))
            throw new InvalidOperationException($"session \"{session.Id}\" is not entered in the store");
        if (entry.Announced)
            return;
        entry.Announced = true;
        Ctx.Emit(new SessionCreatedNotification(session));
    }

    private void PublishEvent(Session session, SessionEvent sessionEvent)
    {
        Ctx.Events.Emit(Ctx, new SessionEventNotification(session, sessionEvent));
    }

    private void Detach(Session session)
    {
        if (!_sessions.Remove(session.Id))
            return;
        Ctx.Emit(new SessionDisposedNotification(session));
    }

    public async Task Flush(Session session)
        => await Ctx.Events.Parallel(Ctx, new SessionFlushNotification(session));

    public Session? Get(SessionId id)
        => _sessions.TryGetValue(id, out var entry) ? entry.Session : null;

    public IReadOnlyList<Session> List() => _sessions.Values.Select(entry => entry.Session).ToList();

    private sealed class SessionDetach(Action detach) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            detach();
        }
    }
}
