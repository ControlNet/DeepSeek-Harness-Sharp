using Dsh.GitInspect;

namespace Dsh.Tui;

/** 右栏 Git 变更只展示、排序和截断共享统计，不解析 Git 输出或启动进程。 */
public static class GitPanel
{
    public const int MaxRows = 12;

    /** 首行是来源，其余为共享的文件名和行数；控制字符转义后显示，避免文件名改变面板布局。 */
    public static IReadOnlyList<string> Format(GitDiffSummary summary, string source)
    {
        var ordered = summary.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        var result = new List<string> { source };
        result.AddRange(ordered.Take(MaxRows).Select(file =>
        {
            var path = DisplayPath(file.Path);
            return file.IsBinary ? $"{path} (binary)" : $"{path} {file.Added}+ {file.Deleted}-";
        }));
        if (ordered.Count > MaxRows)
            result.Add($"~ (+{ordered.Count - MaxRows})");
        return result;
    }

    private static string DisplayPath(string path)
        => path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
}
