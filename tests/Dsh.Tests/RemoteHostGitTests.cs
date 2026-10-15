using Dsh.GitInspect;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/** host.git.* against a real host and a real repository with linked worktrees. */
public sealed class RemoteHostGitTests : IDisposable
{
    private readonly GitTestRepository _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    [Fact(Timeout = 60000)]
    public async Task StatusDiffAndLog_DescribeTheRepository()
    {
        var first = _repo.Commit(("a.txt", "one\n"));
        _repo.Commit(("b.txt", "two\n"));
        _repo.Write("a.txt", "one\nchanged\n");
        _repo.Write("new.txt", "fresh\n");
        await using var host = await HarnessHostFixture.CreateAsync("mgs", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git", Ct);

        var status = await client.GitStatusAsync(_repo.Root, Ct);
        var diff = await client.GitDiffAsync(new RemoteGitDiffRequest(_repo.Root, GitDiffTarget.WorkingTree), Ct);
        var commitDiff = await client.GitDiffAsync(new RemoteGitDiffRequest(_repo.Root, GitDiffTarget.OfCommit(first)), Ct);
        var log = await client.GitLogAsync(new RemoteGitLogRequest(_repo.Root, MaxCount: 1), Ct);

        Assert.True(client.Supports(HostCapabilities.GitInspect));
        Assert.Equal("main", status.Branch.Head);
        Assert.Equal(new GitFileChange("a.txt", GitChangeKind.Modified), Assert.Single(status.Unstaged));
        Assert.Equal(["new.txt"], status.Untracked);
        var patch = Assert.Single(diff.Files);
        Assert.Contains("+changed", patch.Patch, StringComparison.Ordinal);
        Assert.Equal(GitChangeKind.Added, Assert.Single(commitDiff.Files).Kind);
        Assert.Equal("commit 2", Assert.Single(log).Subject);
    }

    [Fact(Timeout = 60000)]
    public async Task Worktrees_AreListedAddedAndRemoved()
    {
        _repo.Commit(("a.txt", "one\n"));
        await using var host = await HarnessHostFixture.CreateAsync("mgw", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git", Ct);
        var path = Path.Combine(_repo.Sandbox, "feature-wt");

        var added = await client.AddWorktreeAsync(new RemoteGitWorktreeAdd(_repo.Root, path, NewBranch: "feature"), Ct);
        var listed = await client.ListWorktreesAsync(_repo.Root, Ct);
        await client.RemoveWorktreeAsync(new RemoteGitWorktreeRemove(_repo.Root, path), Ct);
        var after = await client.ListWorktreesAsync(_repo.Root, Ct);

        Assert.Equal((path, "feature"), (added.Path, added.Branch));
        Assert.Equal([true, false], listed.Select(worktree => worktree.IsMain));
        Assert.False(Directory.Exists(path));
        Assert.Single(after);
    }

    [Fact(Timeout = 60000)]
    public async Task ProjectsOutsideTheAllowedRoots_AreRejected()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses POSIX system paths");
        await using var host = await HarnessHostFixture.CreateAsync("mgr", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git", Ct);

        var error = await Assert.ThrowsAsync<JsonRpcException>(() => client.GitStatusAsync("/etc", Ct));
        await Assert.ThrowsAsync<JsonRpcException>(() => client.AddWorktreeAsync(new RemoteGitWorktreeAdd(_repo.Root, "/etc/dsh-worktree"), Ct));

        Assert.Contains("outside", error.Message, StringComparison.Ordinal);
    }
}
