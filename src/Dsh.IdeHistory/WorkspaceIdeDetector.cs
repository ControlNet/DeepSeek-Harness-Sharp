namespace Dsh.IdeHistory;

/** 工作区里检测到的 IDE 项目特征(可多选: 一个仓库可能同时有 .vscode/ 与 .idea/)。 */
[Flags]
public enum IdeWorkspaceKind
{
    None = 0,
    VsCode = 1,
    JetBrains = 2,
    Zed = 4,
}

/**
 * 工作区门控(§3): 从 cwd 向上找 IDE 项目标记, 只有命中的 IDE 才启用对应 provider 的工作区级查询;
 * 无标记就完全不启用(不做全局扫描, 也不提供强制全局开关)。
 */
public static class WorkspaceIdeDetector
{
    /** 向上的搜索层数上限: 避免在超长路径/符号链接环上无限找。 */
    public const int MaxDepth = 24;

    public static IdeWorkspaceKind Detect(string? workspaceRoot)
        => string.IsNullOrWhiteSpace(workspaceRoot)
            ? IdeWorkspaceKind.None
            : Detect(new DirectoryInfo(Path.GetFullPath(workspaceRoot)));

    private static IdeWorkspaceKind Detect(DirectoryInfo? directory)
    {
        var kind = IdeWorkspaceKind.None;
        for (var depth = 0; directory is not null && depth < MaxDepth; depth++, directory = directory.Parent)
        {
            kind |= DetectAt(directory);
            // 到项目边界(.git)就停: 否则会从 $HOME 之类无关的上层目录继承标记(例如用户主目录恰好有 .vscode)。
            if (directory.Parent is null || Directory.Exists(Path.Combine(directory.FullName, ".git")))
                break;
        }
        return kind;
    }

    private static IdeWorkspaceKind DetectAt(DirectoryInfo directory)
    {
        // 工作区目录可能还不存在(或已被删): 它自己没有标记, 但仍要看上层。
        if (!directory.Exists)
            return IdeWorkspaceKind.None;
        var kind = IdeWorkspaceKind.None;
        if (Directory.Exists(Path.Combine(directory.FullName, ".vscode"))
            || File.Exists(Path.Combine(directory.FullName, ".vscode", "settings.json"))
            || directory.EnumerateFiles("*.code-workspace").Any())
            kind |= IdeWorkspaceKind.VsCode;
        if (Directory.Exists(Path.Combine(directory.FullName, ".idea")) || directory.EnumerateFiles("*.iml").Any())
            kind |= IdeWorkspaceKind.JetBrains;
        if (Directory.Exists(Path.Combine(directory.FullName, ".zed")))
            kind |= IdeWorkspaceKind.Zed;
        return kind;
    }
}
