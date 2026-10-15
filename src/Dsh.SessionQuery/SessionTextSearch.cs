using Dsh.Core;
using Dsh.Llm;
using Microsoft.Data.Sqlite;

namespace Dsh.SessionQuery;

/** 会话事件文本的检索后端:有 SQLite 时用 FTS5,缺失时用进程内匹配;数据始终来自本地会话文件。 */
public interface ISessionTextSearch : IDisposable
{
    void IndexSession(Session session);

    IReadOnlyList<SessionQueryHit> Search(string query, int limit);
}

/** 后端选择:SQLite/FTS5 只是加速手段,装不上就换本地匹配,不阻断插件激活。 */
internal static class SessionTextSearchFactory
{
    public static ISessionTextSearch Create(string databasePath)
    {
        try
        {
            return new FtsSessionTextSearch(databasePath);
        }
        catch (Exception error) when (IsUnavailable(error))
        {
            return new InMemorySessionTextSearch();
        }
    }

    private static bool IsUnavailable(Exception error)
        => error is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or PlatformNotSupportedException
            or TypeInitializationException      // native sqlite 缺失时 static ctor 失败会包成这个
            or SqliteException;                 // 例如该 sqlite 没编进 FTS5

    /** 按空白切词:FTS5 的 MATCH 表达式与本地匹配共用同一分词视角。 */
    public static IReadOnlyList<string> Split(string query)
        => [.. query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

    /** 去掉 FTS 操作符字符,得到本地匹配用的裸词。 */
    public static string Bare(string token) => token.Trim(Operators);

    private static readonly char[] Operators = ['-', '+', '"', '\'', '(', ')', '[', ']', '*', '^', ':'];
}

/** 可检索的事件文本:只取消息类事件的正文块,思考/工具参数不进索引。 */
internal static class SessionEventText
{
    public static string Of(SessionEvent sessionEvent)
    {
        var builder = new System.Text.StringBuilder();
        switch (sessionEvent.Data)
        {
            case UserMessagePayload user:
                Append(builder, user.Message.Content);
                break;
            case AssistantMessagePayload assistant:
                Append(builder, assistant.Message.Content);
                break;
            case ToolResultPayload toolResult:
                Append(builder, toolResult.Message.Content);
                break;
        }
        return builder.ToString();
    }

    private static void Append(System.Text.StringBuilder builder, IReadOnlyList<ContentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is TextBlock text)
                builder.Append(text.Text).Append('\n');
        }
    }
}
