using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Dsh.Gui.ViewModels;

namespace Dsh.Gui.Views;

/**
 * 文件浏览与轻量编辑视图; DataContext 为 FilesViewModel(由集成方构造并调用 LoadAsync)。
 * 文本编辑器是 AvaloniaEdit 的 TextEditor(行号 + 内置 xshd 语法高亮), 它的 Text 不可绑定, 所以在这里与 FileEditorViewModel.Text 双向同步。
 * 本机文件的选择(上传)与保存位置(下载)走 TopLevel 的 StorageProvider, 选好的本地路径交给视图模型。
 */
public sealed partial class FilesView : UserControl
{
    private FilesViewModel? _viewModel;
    private FileEditorViewModel? _editor;
    private bool _syncing;

    public FilesView()
    {
        InitializeComponent();
        TextEditor.TextChanged += OnEditorTextChanged;
        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as FilesViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        AttachEditor(_viewModel?.Editor);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilesViewModel.Editor))
            AttachEditor(_viewModel?.Editor);
    }

    private void AttachEditor(FileEditorViewModel? editor)
    {
        _editor?.TextReloaded -= ShowEditorText;
        _editor = editor;
        _editor?.TextReloaded += ShowEditorText;
        ShowEditorText();
    }

    /** 打开或重新加载后把视图模型的文本整体放进编辑器(不算用户修改)。 */
    private void ShowEditorText()
    {
        _syncing = true;
        TextEditor.Text = _editor?.Text ?? "";
        _syncing = false;
        ApplyHighlighting();
    }

    private void ApplyHighlighting()
        => TextEditor.SyntaxHighlighting = _editor is null ? null : EditorHighlighting.For(_editor.Extension, ActualThemeVariant == ThemeVariant.Dark);

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (!_syncing && _editor is not null)
            _editor.Text = TextEditor.Text;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel?.OpenCommand.CanExecute(null) == true)
            _viewModel.OpenCommand.Execute(null);
    }

    private async void OnUploadClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择要上传的文件", AllowMultiple = true });
        List<string> paths = [.. files.Select(file => file.TryGetLocalPath()).OfType<string>()];
        if (paths.Count > 0)
            await _viewModel.UploadAsync(paths);
    }

    private async void OnDownloadSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.Selected is { IsDirectory: false, IsPlaceholder: false } node)
            await DownloadAsync(node.Path, node.Name);
    }

    private async void OnDownloadOpenedClick(object? sender, RoutedEventArgs e)
    {
        if (_editor is not null)
            await DownloadAsync(_editor.Path, _editor.FileName);
    }

    private async Task DownloadAsync(string remotePath, string name)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "下载到本机", SuggestedFileName = name });
        if (file?.TryGetLocalPath() is { } localPath)
            await _viewModel.DownloadAsync(remotePath, localPath);
    }
}
