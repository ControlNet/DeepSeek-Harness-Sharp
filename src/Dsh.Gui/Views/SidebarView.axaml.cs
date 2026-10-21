using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dsh.Gui.ViewModels;
using Dsh.Llm;

namespace Dsh.Gui.Views;

public sealed partial class SidebarView : UserControl
{
    private MainViewModel? _viewModel;
    private bool _locatePending;
    private MenuFlyout? _sessionMenu;
    private MenuItem? _openSessionItem;
    private MenuItem? _renameSessionItem;
    private MenuItem? _deleteSessionItem;

    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.FileSystemNodes.CollectionChanged -= OnFileSystemNodesChanged;
        }
        _viewModel = DataContext as MainViewModel;
        _sessionMenu = null;
        _openSessionItem = null;
        _renameSessionItem = null;
        _deleteSessionItem = null;
        _locatePending = false;
        if (_viewModel is null)
            return;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.FileSystemNodes.CollectionChanged += OnFileSystemNodesChanged;
        LocateCurrentSession();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsFileSystemView) or nameof(MainViewModel.CurrentSessionId))
            LocateCurrentSession();
    }

    private void OnFileSystemNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_locatePending)
            LocateCurrentSession();
    }

    /** 文件系统视图下把树定位到当前会话所在目录: 展开祖先链, 选中并滚入视野; 会话尚未落盘时保持原样。 */
    private void LocateCurrentSession()
    {
        if (_viewModel is not { IsFileSystemView: true } viewModel)
            return;
        if (viewModel.FileSystemNodes.Count == 0)
        {
            _locatePending = true;
            return;
        }
        _locatePending = false;
        var path = FindSessionPath(viewModel.FileSystemNodes, viewModel.CurrentSessionId);
        if (path is null)
            return;
        foreach (var ancestor in path.SkipLast(1))
            ancestor.IsExpanded = true;
        var node = path[^1];
        foreach (var session in node.Sessions)
            session.IsSelected = session.SessionId == viewModel.CurrentSessionId;
        Dispatcher.UIThread.Post(() =>
        {
            FileTree.SelectedItem = node;
            FileTree.ScrollIntoView(node);
        }, DispatcherPriority.Background);
    }

    private static List<WorkspaceNodeViewModel>? FindSessionPath(IEnumerable<WorkspaceNodeViewModel> nodes, SessionId id)
    {
        foreach (var node in nodes)
        {
            if (node.Sessions.Any(session => session.SessionId == id))
                return [node];
            if (FindSessionPath(node.Children, id) is { } childPath)
                return [node, .. childPath];
        }
        return null;
    }

    /** 会话行的操作菜单只在点“更多”时创建一次并复用; 放进每行 DataTemplate 会让 248 个会话各建 4 个 MenuItem。 */
    private void OnSessionMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SessionNodeViewModel node } button || _viewModel is not { } viewModel)
            return;
        EnsureSessionMenu(viewModel);
        _openSessionItem!.CommandParameter = node;
        _renameSessionItem!.CommandParameter = node;
        _deleteSessionItem!.CommandParameter = node;
        _sessionMenu!.ShowAt(button);
    }

    private void EnsureSessionMenu(MainViewModel viewModel)
    {
        if (_sessionMenu is not null)
            return;
        _openSessionItem = new MenuItem { Header = "打开", Command = viewModel.OpenSessionCommand };
        _renameSessionItem = new MenuItem { Header = "重命名", Command = viewModel.BeginRenameCommand };
        _deleteSessionItem = new MenuItem { Header = "删除", Command = viewModel.DeleteSessionCommand };
        _sessionMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        _sessionMenu.Items.Add(_openSessionItem);
        _sessionMenu.Items.Add(_renameSessionItem);
        _sessionMenu.Items.Add(new MenuItem { Header = "导出日志", Command = viewModel.ExportSessionLogCommand });
        _sessionMenu.Items.Add(_deleteSessionItem);
    }

    private void OnRenameAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionNodeViewModel node } || DataContext is not MainViewModel viewModel)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            viewModel.CommitRenameCommand.Execute(node);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            viewModel.CancelRenameCommand.Execute(node);
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionNodeViewModel node } || DataContext is not MainViewModel viewModel)
            return;
        if (node.IsRenaming)
            viewModel.CommitRenameCommand.Execute(node);
    }
}
