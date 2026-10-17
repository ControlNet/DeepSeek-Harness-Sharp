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
        var aliceChanges = alice.NotificationChangesAsync(Ct);
        var bobChanges = bob.NotificationChangesAsync(Ct);
        var aliceInitial = await RemoteHostMultiClientTests.NextAsync(aliceChanges);
        var bobInitial = await RemoteHostMultiClientTests.NextAsync(bobChanges);
        Assert.Empty(aliceInitial.Page.Items);
        Assert.Equal((0, 0), (aliceInitial.Page.Total, aliceInitial.Page.UnreadCount));
        Assert.Null(aliceInitial.Added);
        Assert.Null(aliceInitial.ReadIds);
        Assert.Empty(bobInitial.Page.Items);
        Assert.Equal((0, 0), (bobInitial.Page.Total, bobInitial.Page.UnreadCount));
        var session = await alice.CreateSessionAsync(host.Environment.App.Home.Root, RemoteHostMultiClientTests.Selection, RemoteHostMultiClientTests.Spec, Ct);

        var decision = RemoteHostMultiClientTests.RaiseApprovalAsync(host, RemoteHostMultiClientTests.Agent(host, session.Id), "bash");
        var request = Assert.Single((await RemoteHostMultiClientTests.NextAsync(alice.PendingChangesAsync(Ct), change => change.Pending.Approvals.Count > 0)).Pending.Approvals);
        var addedChange = await RemoteHostMultiClientTests.NextAsync(aliceChanges, change => change.Added?.Id == request.RequestId);
        var bobAddedChange = await RemoteHostMultiClientTests.NextAsync(bobChanges, change => change.Added?.Id == request.RequestId);
        var added = Assert.IsType<NotificationRecord>(addedChange.Added);
        var seenByBob = Assert.IsType<NotificationRecord>(bobAddedChange.Added);
        Assert.Equal(added, Assert.Single(addedChange.Page.Items));
        Assert.Equal(seenByBob, Assert.Single(bobAddedChange.Page.Items));
        Assert.Equal((1, 1), (addedChange.Page.Total, addedChange.Page.UnreadCount));
        Assert.Equal(1, bobAddedChange.Page.UnreadCount);
        Assert.True(await alice.RespondApprovalAsync(request.RequestId, false, cancellationToken: Ct));
        Assert.Equal(Dsh.Core.ApprovalOutcome.Rejected, await decision.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        Assert.True(alice.Supports(NotificationsProtocol.Capability));
        Assert.Equal(added, seenByBob);
        Assert.Equal((request.RequestId, session.Id, NotificationKind.ApprovalRequest), (added.Id, added.SessionId, added.Kind));
        Assert.Equal(new NotificationTarget(NotificationView.PendingRequest, request.RequestId), added.Target);
        Assert.Equal(1, await alice.MarkNotificationsReadAsync(new RemoteNotifyMarkRead([added.Id]), Ct));
        var changed = await RemoteHostMultiClientTests.NextAsync(bobChanges, change => change.ReadIds?.Contains(added.Id) == true);
        Assert.Equal([added.Id], changed.ReadIds);
        Assert.Equal(0, changed.Page.UnreadCount);
        Assert.True(Assert.Single(changed.Page.Items).IsRead);
        Assert.True(Assert.Single((await bob.ListNotificationsAsync(cancellationToken: Ct)).Items).IsRead);

        await host.RestartHostAsync(Ct);

        var after = await host.ConnectAsync("after", Ct);
        var page = await after.ListNotificationsAsync(cancellationToken: Ct);
        var kept = Assert.Single(page.Items);
        Assert.Equal((added.Id, true, 0), (kept.Id, kept.IsRead, page.UnreadCount));
        var restored = await RemoteHostMultiClientTests.NextAsync(after.NotificationChangesAsync(Ct));
        Assert.Equal(page.Items, restored.Page.Items);
        Assert.Equal((page.Total, page.UnreadCount), (restored.Page.Total, restored.Page.UnreadCount));
        Assert.Null(restored.Added);
        Assert.Null(restored.ReadIds);
    }

    [Fact(Timeout = 60000)]
    public async Task SessionExit_IsRecordedWithItsExitCode_AndListFiltersAndPages()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses a POSIX shell");
        await using var host = await HarnessHostFixture.CreateAsync("mne", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("solo", Ct);
        var changes = client.NotificationChangesAsync(Ct);
        Assert.Empty((await RemoteHostMultiClientTests.NextAsync(changes)).Page.Items);

        var first = await client.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", ["-c", "sleep 1; exit 3"], Title: "short job"), Ct);
        var exitedChange = await RemoteHostMultiClientTests.NextAsync(changes, change => change.Added?.SessionId == first);
        var exited = Assert.IsType<NotificationRecord>(exitedChange.Added);
        Assert.Equal(exited, Assert.Single(exitedChange.Page.Items));
        Assert.Equal((1, 1), (exitedChange.Page.Total, exitedChange.Page.UnreadCount));
        var second = await client.StartPtyAsync(new RemotePtyStartRequest("/bin/sh", ["-c", "sleep 1; exit 4"], Title: "second job"), Ct);
        var secondChange = await RemoteHostMultiClientTests.NextAsync(changes, change => change.Added?.SessionId == second);
        Assert.Equal((2, 2), (secondChange.Page.Total, secondChange.Page.UnreadCount));
        Assert.Contains(secondChange.Page.Items, record => record.SessionId == second);
        Assert.Equal(1, await client.MarkNotificationsReadAsync(new RemoteNotifyMarkRead(All: true, SessionId: second), Ct));

        var unread = await client.ListNotificationsAsync(new RemoteNotifyQuery(UnreadOnly: true), Ct);
        var paged = await client.ListNotificationsAsync(new RemoteNotifyQuery(Kinds: [NotificationKind.SessionExited], Offset: 1, Limit: 1), Ct);

        Assert.Equal((NotificationKind.SessionExited, "short job exited", "exit code 3"), (exited.Kind, exited.Title, exited.Body));
        Assert.Equal(first, Assert.Single(unread.Items).SessionId);
        Assert.Equal((2, 1), (paged.Total, paged.UnreadCount));
        Assert.Equal(first, Assert.Single(paged.Items).SessionId);
    }
}
