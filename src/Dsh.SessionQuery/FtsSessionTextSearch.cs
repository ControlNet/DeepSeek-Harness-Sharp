using Dsh.Core;
using Microsoft.Data.Sqlite;

namespace Dsh.SessionQuery;

/** SQLite FTS5 后端:一次性把会话事件灌进内存表,用 MATCH 查询。 */
public sealed class FtsSessionTextSearch : ISessionTextSearch
{
    private readonly SqliteConnection _connection;

    static FtsSessionTextSearch()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
    }

    public FtsSessionTextSearch(string databasePath)
    {
        _connection = new SqliteConnection(databasePath == ":memory:" ? "Data Source=:memory:" : $"Data Source={databasePath}");
        _connection.Open();
        Initialize();
    }

    public void IndexSession(Session session)
    {
        using var delete = _connection.CreateCommand();
        delete.CommandText = "DELETE FROM docs WHERE session_id = $id";
        delete.Parameters.AddWithValue("$id", session.Id.Value);
        delete.ExecuteNonQuery();

        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO docs(session_id, seq, type, text)
            VALUES ($id, $seq, $type, $text)
            """;
        var idParameter = insert.Parameters.Add("$id", SqliteType.Text);
        var seqParameter = insert.Parameters.Add("$seq", SqliteType.Integer);
        var typeParameter = insert.Parameters.Add("$type", SqliteType.Text);
        var textParameter = insert.Parameters.Add("$text", SqliteType.Text);

        foreach (var sessionEvent in session.SnapshotEvents())
        {
            var text = SessionEventText.Of(sessionEvent);
            if (text.Length == 0)
                continue;
            idParameter.Value = session.Id.Value;
            seqParameter.Value = sessionEvent.Seq;
            typeParameter.Value = sessionEvent.Type;
            textParameter.Value = text;
            insert.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SessionQueryHit> Search(string query, int limit)
    {
        var match = BuildMatch(query);
        if (match.Length == 0)
            return [];
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, seq, type, snippet(docs, 0, '[', ']', '...', 8) AS snippet
            FROM docs
            WHERE docs MATCH $query
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", match);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var hits = new List<SessionQueryHit>();
        while (reader.Read())
        {
            hits.Add(new SessionQueryHit(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }
        return hits;
    }

    /** 用户输入按词逐个加引号,避免 FTS5 语法字符(-、OR、引号)被当作操作符或报错。 */
    private static string BuildMatch(string query)
    {
        var tokens = SessionTextSearchFactory.Split(query)
            .Select(token => "\"" + token.Replace("\"", "\"\"") + "\"");
        return string.Join(" AND ", tokens);
    }

    public void Dispose() => _connection.Dispose();

    private void Initialize()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS docs USING fts5(
              text,
              session_id UNINDEXED,
              seq UNINDEXED,
              type UNINDEXED,
              tokenize = 'unicode61'
            );
            """;
        command.ExecuteNonQuery();
    }
}
