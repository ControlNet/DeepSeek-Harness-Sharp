using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/**
 * 文件树的一个节点(远端文件或目录)。
 * 目录在第一次展开时才列举子项; 未列举前挂一个"加载中"占位子项, 让树控件显示展开箭头。
 */
public sealed partial class FileNodeViewModel : ObservableObject
{
    private const string PlaceholderName = "加载中…";

    private readonly FilesViewModel? _owner;

    /** owner 为 null 表示"加载中"占位节点。 */
    internal FileNodeViewModel(FilesViewModel? owner, RemoteDirectoryEntry entry, FileNodeViewModel? parent)
    {
        _owner = owner;
        Entry = entry;
        Parent = parent;
        if (entry.IsDirectory && owner is not null)
            Children.Add(new FileNodeViewModel(null, new RemoteDirectoryEntry(PlaceholderName, "", false), this));
    }

    public RemoteDirectoryEntry Entry
    {
        get;
        private set
        {
            field = value;
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(ModifiedText));
        }
    }

    public string Name => Entry.Name;

    public string Path => Entry.Path;

    public bool IsDirectory => Entry.IsDirectory;

    public bool IsPlaceholder => _owner is null;

    public FileNodeViewModel? Parent { get; }

    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    /** 子项已经列举过(之后展开不再自动列举, 刷新时重新列举)。 */
    public bool IsLoaded { get; private set; }

    public string SizeText => IsDirectory || Entry.Size is not { } size ? "" : FileSizeText.Format(size);

    public string ModifiedText => Entry.ModifiedAt is { } modified && !IsPlaceholder ? FileSizeText.Time(modified) : "";

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !IsLoaded && _owner is not null)
            _ = _owner.LoadChildrenAsync(this);
    }

    /** 用新的列举结果替换子项: 同路径同类型的旧节点原样保留(保持其展开状态与已加载的子树), 只更新大小与时间。 */
    internal void ReplaceChildren(IReadOnlyList<RemoteDirectoryEntry> entries)
    {
        var existing = Children.Where(child => !child.IsPlaceholder).ToDictionary(child => child.Path, StringComparer.Ordinal);
        var children = entries.Select(entry => Reuse(existing.GetValueOrDefault(entry.Path), entry) ?? new FileNodeViewModel(_owner, entry, this)).ToList();
        Children.Clear();
        foreach (var child in children)
            Children.Add(child);
        IsLoaded = true;
    }

    private static FileNodeViewModel? Reuse(FileNodeViewModel? node, RemoteDirectoryEntry entry)
    {
        if (node is null || node.IsDirectory != entry.IsDirectory)
            return null;
        node.Entry = entry;
        return node;
    }
}
