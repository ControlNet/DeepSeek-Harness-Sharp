using Dsh.Core;
using Dsh.Llm;

namespace Dsh.SessionGraph;

/**
 * 单个会话日志的事件图: 按 seq 顺序逐条吸收事件, 增量维护节点、边与 surface 状态。
 * surface 折叠交给 Surface.Manager, 配对关系来自 SessionEventRelations 的登记; 不是线程安全的, 由调用方加锁。
 */
internal sealed class SessionLogGraph
{
    private readonly List<SessionEvent> _events = [];
    private readonly Surface.Manager _surface;
    private readonly HashSet<long> _shadowed = [];
    private readonly Dictionary<long, List<SessionEventEdge>> _outgoing = [];
    private readonly Dictionary<long, List<SessionEventEdge>> _incoming = [];
    private readonly Dictionary<SessionEventCorrelation, long> _openers = [];
    private readonly Dictionary<SessionEventCorrelation, List<(long From, SessionEventEdgeKind Kind)>> _awaitingOpener = [];
    private readonly Dictionary<SessionId, long> _spawns = [];
    private readonly long _inheritedEventCount;

    public SessionLogGraph(long inheritedEventCount)
    {
        _inheritedEventCount = inheritedEventCount;
        _surface = new Surface.Manager(() => _events);
    }

    public long Count => _events.Count;

    /** 吸收 log 中尚未吸收的尾部; log 必须是本图已吸收事件的延续。 */
    public void CatchUp(IReadOnlyList<SessionEvent> log)
    {
        for (var index = _events.Count; index < log.Count; index++)
            Append(log[index]);
    }

    private void Append(SessionEvent sessionEvent)
    {
        _events.Add(sessionEvent);
        IReadOnlyList<long> shadowed = Surface.IsReplacementSurfaceEvent(sessionEvent) ? _surface.Replacements[^1].ShadowedSeqs : [];
        foreach (var seq in shadowed)
        {
            _shadowed.Add(seq);
            AddEdge(new SessionEventEdge(SessionEventEdgeKind.Replaces, sessionEvent.Seq, seq));
        }
        foreach (var source in sessionEvent.SourceEventSeqs ?? [])
        {
            if (!shadowed.Contains(source))
                AddEdge(new SessionEventEdge(SessionEventEdgeKind.Cites, sessionEvent.Seq, source));
        }
        foreach (var relation in SessionEventRelations.Of(sessionEvent.Data))
            Relate(sessionEvent.Seq, relation);
    }

    public SessionEventNode? Node(long seq)
    {
        if (seq < 0 || seq >= _events.Count)
            return null;
        var sessionEvent = _events[(int)seq];
        var surface = !Surface.IsSurfaceEvent(sessionEvent) ? SessionEventSurface.LogOnly
            : _shadowed.Contains(seq) ? SessionEventSurface.Shadowed
            : SessionEventSurface.Current;
        return new SessionEventNode(sessionEvent, surface, seq < _inheritedEventCount);
    }

    public IReadOnlyList<SessionEventEdge> EdgesFrom(long seq) => _outgoing.TryGetValue(seq, out var edges) ? [.. edges] : [];

    public IReadOnlyList<SessionEventEdge> EdgesTo(long seq) => _incoming.TryGetValue(seq, out var edges) ? [.. edges] : [];

    /** 启动子会话 child 的事件 seq; 日志里没有对应的派生事件时为 null。 */
    public long? SpawnOf(SessionId child) => _spawns.TryGetValue(child, out var seq) ? seq : null;

    private void Relate(long seq, SessionEventRelation relation)
    {
        switch (relation)
        {
            case SessionEventRelation.Opens opens:
                _openers[opens.Key] = seq;
                if (_awaitingOpener.Remove(opens.Key, out var waiting))
                {
                    foreach (var (from, kind) in waiting)
                        AddEdge(new SessionEventEdge(kind, from, seq));
                }
                break;
            case SessionEventRelation.RefersTo refers when _openers.TryGetValue(refers.Key, out var opener):
                AddEdge(new SessionEventEdge(refers.Kind, seq, opener));
                break;
            case SessionEventRelation.RefersTo refers:
                if (!_awaitingOpener.TryGetValue(refers.Key, out var pending))
                    _awaitingOpener[refers.Key] = pending = [];
                pending.Add((seq, refers.Kind));
                break;
            case SessionEventRelation.Spawns spawns:
                _spawns[spawns.Child] = seq;
                break;
        }
    }

    private void AddEdge(SessionEventEdge edge)
    {
        if (!_outgoing.TryGetValue(edge.From, out var outgoing))
            _outgoing[edge.From] = outgoing = [];
        outgoing.Add(edge);
        if (!_incoming.TryGetValue(edge.To, out var incoming))
            _incoming[edge.To] = incoming = [];
        incoming.Add(edge);
    }
}
