using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.Workspace;
using Dsh.Interaction;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/**
 * One approval or question waiting on some machine, answerable from the notification center and from its session's view.
 * Every client sees the same request; the first answer wins and the host's resolution removes it everywhere, so this item only sends the answer.
 */
public sealed partial class PendingDecisionViewModel(FleetPendingItem item, Func<IWorkspaceMachine?> machine) : ObservableObject
{
    public FleetPendingItem Item { get; } = item;

    public string Machine => Item.Machine;

    public string RequestId => Item.RequestId;

    public string SessionId => Item.SessionId;

    public bool IsApproval => Item.Kind == PendingKinds.Approval;

    public bool IsQuestion => Item.Kind == PendingKinds.Question;

    public string KindLabel => IsApproval ? "审批" : "提问";

    public string Title => Item.Approval is { } approval
        ? $"{approval.Tool} {ApprovalHints.PrimaryArgument(approval.Tool, approval.Arguments)}".Trim()
        : Item.Question?.Questions.FirstOrDefault()?.Question ?? "";

    public string Detail => Item.Approval?.Reason ?? Item.Question?.Questions.FirstOrDefault()?.Detail ?? "";

    public bool HasDetail => Detail.Length > 0;

    /** The session's title as the dashboard knows it; set by the owner, since a request only carries the session id. */
    [ObservableProperty]
    private string _sessionTitle = "";

    [ObservableProperty]
    private bool _isBusy;

    /** The answer was rejected by the host because another client answered first or the request was cancelled. */
    public event Action<PendingDecisionViewModel>? Superseded;

    /** Questions are answered in the decision dialog; the owner shows it and passes the answer back. */
    public event Func<DecisionViewModel, Task<object?>>? AnswerRequested;

    public event Action<PendingDecisionViewModel>? OpenRequested;

    [RelayCommand]
    private Task AllowAsync() => RespondApprovalAsync(true);

    [RelayCommand]
    private Task RejectAsync() => RespondApprovalAsync(false);

    [RelayCommand]
    private void Open() => OpenRequested?.Invoke(this);

    [RelayCommand]
    private async Task AnswerAsync()
    {
        if (Item.Question is not { } question || AnswerRequested is not { } ask || machine() is not { } connection)
            return;
        var decision = DecisionViewModel.ForQuestion(new AskUserQuestionRequest([.. question.Questions.Select(ToItem)]));
        if (await ask(decision) is not AskUserQuestionAnswer answer)
            return;
        await SendAsync(connection, (host, token) => host.RespondQuestionAsync(
            RequestId, [.. answer.Answers.Select(entry => new RemoteQuestionAnswerItem(entry.Id, entry.Selected, entry.Custom))], token));
    }

    private Task RespondApprovalAsync(bool allow)
        => machine() is { } connection
            ? SendAsync(connection, (host, token) => host.RespondApprovalAsync(RequestId, allow, null, token))
            : Task.CompletedTask;

    /** Answers on the machine's current link, waiting for it while the machine reconnects. */
    private async Task SendAsync(IWorkspaceMachine connection, Func<IWorkspaceHost, CancellationToken, Task<bool>> respond)
    {
        IsBusy = true;
        try
        {
            var lease = await connection.WaitConnectedAsync(CancellationToken.None);
            if (!await respond(lease.Client, CancellationToken.None))
                Superseded?.Invoke(this);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static AskUserQuestionItem ToItem(RemoteQuestionItem item)
        => new(
            item.Id,
            item.Question,
            item.Detail,
            item.Header,
            [.. item.Options.Select(option => new AskUserQuestionOption(option.Label, option.Description))],
            item.MultiSelect,
            item.ApproveLabel is { } approve ? new AskUserQuestionIntent(AskUserQuestionIntentKind.PlanReview, approve) : null);
}
