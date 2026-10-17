using Dsh.Core;

namespace Dsh.SessionQuery;

/** 无 SQLite/FTS5 时在会话事件文本上做不区分大小写的子串匹配,多个词以 AND 组合。 */
public sealed class InMemorySessionTextSearch : ISessionTextSearch
{
    private const int SnippetWindow = 60;

    private readonly List<IndexedSession> _sessions = [];

    public void IndexSession(Session session)
    {
        var entries = new List<IndexedEvent>();
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            var text = SessionEventText.Of(sessionEvent);
            if (text.Length > 0)
                entries.Add(new IndexedEvent(sessionEvent.Seq, sessionEvent.Type, text));
        }
        var id = session.Id.Value;
        var existing = _sessions.FindIndex(indexed => string.Equals(indexed.Id, id, StringComparison.Ordinal));
        if (existing >= 0)
            _sessions[existing] = new IndexedSession(id, entries);
        else
            _sessions.Add(new IndexedSession(id, entries));
    }

    public IReadOnlyList<SessionQueryHit> Search(string query, int limit)
    {
        var terms = SessionTextSearchFactory.Split(query)
            .Select(SessionTextSearchFactory.Bare)
            .Where(term => term.Length > 0)
            .ToList();
        if (terms.Count == 0)
            return [];
        var hits = new List<SessionQueryHit>();
        foreach (var session in _sessions)
        {
            foreach (var entry in session.Entries)
            {
                if (!terms.All(term => entry.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    continue;
                hits.Add(new SessionQueryHit(session.Id, entry.Seq, entry.Type, Snippet(entry.Text, terms[0])));
                if (hits.Count >= limit)
                    return hits;
            }
        }
        return hits;
    }

    public void Dispose()
    {
        _sessions.Clear();
    }

    private static string Snippet(string text, string term)
    {
        var flat = text.Replace('\n', ' ').Trim();
        var start = flat.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return flat.Length <= SnippetWindow * 2 ? flat : string.Concat(flat.AsSpan(0, SnippetWindow * 2), "...");
        var end = Math.Min(flat.Length, start + term.Length);
        var from = Math.Max(0, start - SnippetWindow);
        var to = Math.Min(flat.Length, end + SnippetWindow);
        var prefix = from > 0 ? "..." : "";
        var suffix = to < flat.Length ? "..." : "";
        return $"{prefix}{flat[from..start]}[{flat[start..end]}]{flat[end..to]}{suffix}";
    }

    private sealed record IndexedSession(string Id, List<IndexedEvent> Entries);

    private sealed record IndexedEvent(long Seq, string Type, string Text);
}
