using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Notifications;

namespace Dsh.Gui.ViewModels;

/** One entry of a machine's notification history; Machine is the fleet's name for the machine, not the host's own MachineId. */
public sealed partial class NotificationItemViewModel(string machine, NotificationRecord record) : ObservableObject
{
    public string Machine { get; } = machine;

    public NotificationRecord Record { get; } = record;

    public string Id => Record.Id;

    public string? SessionId => Record.SessionId;

    public NotificationKind Kind => Record.Kind;

    public string KindLabel => SessionPresentation.NotificationKindLabel(Record.Kind);

    public string Title => Record.Title;

    public string Body => Record.Body ?? "";

    public bool HasBody => Body.Length > 0;

    public DateTimeOffset CreatedAt => Record.CreatedAt;

    public string TimeText => RelativeTimeText.Format(Record.CreatedAt);

    public bool HasSession => Record.SessionId is not null;

    [ObservableProperty]
    private bool _isRead = record.IsRead;

    [ObservableProperty]
    private string _sessionTitle = "";

    public bool IsUnread => !IsRead;

    partial void OnIsReadChanged(bool value) => OnPropertyChanged(nameof(IsUnread));
}

/** An entry of a notification filter drop-down: a label and the value it filters by (null = no filter). */
public sealed record NotificationFilterOption(string Label, string? Value)
{
    public override string ToString() => Label;
}
