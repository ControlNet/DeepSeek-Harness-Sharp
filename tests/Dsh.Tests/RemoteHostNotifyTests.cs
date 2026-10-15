using Dsh.Notifications;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** host.notify.* against a real host: approvals and session exits enter the history, read marks are shared, the history survives a restart. */
public sealed class RemoteHostNotifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60000)]
    public async Task TwoClients_SeeTheSameNotifications_AndTheSameReadState_AcrossARestart()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mna", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = await host.ConnectAsync("bob", Ct);
        var session = await alice.CreateSessionAsync(host.Environment.App.Home.Root, RemoteHostMultiClientTests.Selection, RemoteHostMultiClientTests.Spec, Ct);

        var decision = RemoteHostMultiClientTests.RaiseApprovalAsync(host, RemoteHostMultiClientTests.Agent(host, session.Id), "bash");
        var request = await RemoteHostMultiClientTests.NextAsync(alice.ApprovalsAsync(Ct));
        var added = await RemoteHostMultiClientTests.NextAsync(alice.NotificationsAddedAsync(Ct));
        var seenByBob = await RemoteHostMultiClientTests.NextAsync(bob.NotificationsAddedAsync(Ct));
        await alice.RespondApprovalAsync(request.RequestId, false, cancellationToken: Ct);
        await decision.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(alice.Supports(HostCapabilities.NotifyHistory));
        Assert.Equal(added, seenByBob);
        Assert.Equal((request.RequestId, session.Id, NotificationKind.ApprovalRequest), (added.Id, added.SessionId, added.Kind));
        Assert.Equal(new NotificationTarget(NotificationView.PendingRequest, request.RequestId), added.Target);
        Assert.Equal(1, await alice.MarkNotificationsReadAsync(new RemoteNotifyMarkRead([added.Id]), Ct));
        var changed = await RemoteHostMultiClientTests.NextAsync(bob.NotificationChangesAsync(Ct));
        Assert.Equal([added.Id], changed.ReadIds);
        Assert.Equal(0, changed.UnreadCount);
        Assert.True(Assert.Single((await bob.ListNotificationsAsync(cancellationToken: Ct)).Items).IsRead);

        await host.RestartHostAsync(Ct);

        var after = await host.ConnectAsync("after", Ct);
        var page = await after.ListNotificationsAsync(cancellationToken: Ct);
        var kept = Assert.Single(page.Items);
        Assert.Equal((added.Id, true, 0), (kept.Id, kept.IsRead, page.UnreadCount));
    }

    [Fact(Timeout = 60000)]
    public async Task SessionExit_IsRecordedWithItsExitCode_AndListFiltersAndPages()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell");
        await using var host = await HarnessHostFixture.CreateAsync("mne", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("solo", Ct);
        var added = client.NotificationsAddedAsync(Ct);

        var first = await client.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", ["-c", "sleep 1; exit 3"], Title: "short job"), Ct);
        var exited = await RemoteHostMultiClientTests.NextAsync(added, record => record.SessionId == first);
        var second = await client.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", ["-c", "sleep 1; exit 4"], Title: "second job"), Ct);
        await RemoteHostMultiClientTests.NextAsync(added, record => record.SessionId == second);
        Assert.Equal(1, await client.MarkNotificationsReadAsync(new RemoteNotifyMarkRead(All: true, SessionId: second), Ct));

        var unread = await client.ListNotificationsAsync(new RemoteNotifyQuery(UnreadOnly: true), Ct);
        var paged = await client.ListNotificationsAsync(new RemoteNotifyQuery(Kinds: [NotificationKind.SessionExited], Offset: 1, Limit: 1), Ct);

        Assert.Equal((NotificationKind.SessionExited, "short job exited", "exit code 3"), (exited.Kind, exited.Title, exited.Body));
        Assert.Equal(first, Assert.Single(unread.Items).SessionId);
        Assert.Equal((2, 1), (paged.Total, paged.UnreadCount));
        Assert.Equal(first, Assert.Single(paged.Items).SessionId);
    }
}
