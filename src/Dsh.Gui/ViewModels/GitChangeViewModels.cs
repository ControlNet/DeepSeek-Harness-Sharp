using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.GitInspect;

namespace Dsh.Gui.ViewModels;

/** git status 的分组: 已暂存、未暂存、未跟踪、冲突。 */
public enum GitChangeSection
{
    Staged,
    Unstaged,
    Untracked,
    Conflicted,
}

/** 状态列表里的一个文件; Path 相对仓库根目录(git porcelain 的约定)。 */
public sealed partial class GitChangeItemViewModel(GitChangeSection section, string path, GitChangeKind? kind, string? originalPath) : ObservableObject
{
    public GitChangeSection Section => section;

    public string Path => path;

    public string? OriginalPath => originalPath;

    public GitChangeKind? Kind => kind;

    /** 状态字母: M/A/D/R/C/T/U, 未跟踪为 ?。 */
    public string Code => kind switch
    {
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Copied => "C",
        GitChangeKind.TypeChanged => "T",
        GitChangeKind.Unmerged => "U",
        GitChangeKind.Modified => "M",
        _ => "?",
    };

    public string Label => originalPath is null ? path : $"{originalPath} → {path}";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class GitChangeGroupViewModel(GitChangeSection section, string title, IReadOnlyList<GitChangeItemViewModel> items)
{
    public GitChangeSection Section => section;

    public string Header => $"{title}（{items.Count}）";

    public ObservableCollection<GitChangeItemViewModel> Items { get; } = [.. items];

    public bool HasItems => items.Count > 0;
}
