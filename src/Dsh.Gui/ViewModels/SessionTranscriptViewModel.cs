using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Gui.ViewModels;

/**
 * The conversation of a harness or ACP session on any machine, rendered like the chat view from the host's session events.
 * The event subscription follows the session across reconnections from the last seen seq, so nothing is shown twice.
 * Prompts go through host.session.message (harness, with images the prompt refers to) or host.agent.prompt (ACP); cancel is host.agent.cancel.
 */
public sealed partial class SessionTranscriptViewModel : ObservableObject, IDisposable
{
    private readonly MachineConnection _machine;
    private readonly FleetSessionActions _actions;
    private readonly SessionCardViewModel _card;
    private SubagentTranscript _transcript = new();
    private readonly DispatcherBridge<SessionEvent> _events;
    private readonly CancellationTokenSource _lifetime = new();
    private long _renderedSeq;

    public SessionTranscriptViewModel(MachineConnection machine, FleetSessionActions actions, SessionCardViewModel card)
    {
        _machine = machine;
        _actions = actions;
        _card = card;
        _events = new DispatcherBridge<SessionEvent>(Apply);
        _ = FollowAsync(_lifetime.Token);
    }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    /** Why the conversation cannot be shown or the last prompt failed; empty when all is well. */
    [ObservableProperty]
    private string _error = "";

    public bool HasError => Error.Length > 0;

    public string Placeholder => _card.Kind == HostSessionKinds.Harness
        ? "发送给会话（Enter 发送，Shift+Enter 换行；@路径 附带图片）"
        : "发送给 Agent（Enter 发送，Shift+Enter 换行）";

    /** Raised after events were applied, so the view can keep the list scrolled to the end. */
    public event Action? MessagesChanged;

    /** Stops following the session; the source is not disposed because commands still in flight read its token. */
    public void Dispose() => _lifetime.Cancel();

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    private bool CanSend() => !IsSending && Input.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        Input = "";
        IsSending = true;
        try
        {
            await SendTextAsync(text);
            Error = "";
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            Error = $"发送失败: {error.Message}";
            Input = text;
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        try
        {
            var lease = await _machine.WaitConnectedAsync(_lifetime.Token);
            await lease.Client.Agent.CancelAgentAsync(_card.SessionId, _lifetime.Token);
        }
        catch (Exception error) when (error is IOException or JsonRpcException or InvalidOperationException or OperationCanceledException)
        {
            Error = $"取消失败: {error.Message}";
        }
    }

    /** A harness session is resumed first (a no-op while its agent is live), so a prompt to a session without a live agent is not lost. */
    private async Task SendTextAsync(string text)
    {
        if (_card.Kind == HostSessionKinds.Harness)
            await _actions.ResumeAsync(_card.Machine, _card.Session, _lifetime.Token);
        var lease = await _machine.WaitConnectedAsync(_lifetime.Token);
        if (_card.Kind != HostSessionKinds.Harness)
        {
            await lease.Client.Agent.PromptAgentAsync(_card.SessionId, text, _lifetime.Token);
            return;
        }
        var style = RemotePaths.StyleFor(_machine.Info?.Platform ?? "");
        var (remaining, images) = await PromptImages.ExtractRemoteAsync(lease.Client.Files, _card.ProjectPath ?? "", style, text, _lifetime.Token);
        await lease.Client.SendMessageAsync(_card.SessionId, remaining, images, _lifetime.Token);
    }

    private async Task FollowAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _machine.SubscribeAsync(_card.SessionId, 0, cancellationToken))
            {
                if (item.Reset)
                {
                    var snapshot = await (await _machine.WaitConnectedAsync(cancellationToken)).Client.GetSessionSnapshotAsync(_card.SessionId, cancellationToken);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_lifetime.IsCancellationRequested)
                            return;
                        _transcript = new SubagentTranscript();
                        _renderedSeq = 0;
                        Messages.Clear();
                        Apply(snapshot.Events.Select(value => DshJson.Deserialize<SessionEvent>(value.EventJson)).OfType<SessionEvent>().ToList());
                    });
                    continue;
                }
                if (DshJson.Deserialize<SessionEvent>(item.EventJson) is { } sessionEvent)
                    _events.Enqueue(sessionEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (JsonRpcException error)
        {
            Dispatcher.UIThread.Post(() => Error = $"无法读取会话内容: {error.Message}");
        }
    }

    private void Apply(IReadOnlyList<SessionEvent> batch)
    {
        if (_lifetime.IsCancellationRequested)
            return;
        foreach (var sessionEvent in batch)
        {
            if (sessionEvent.Seq < _renderedSeq)
                continue;
            _renderedSeq = sessionEvent.Seq + 1;
            _transcript.Apply(sessionEvent, Messages);
        }
        _transcript.Flush();
        MessagesChanged?.Invoke();
    }
}
