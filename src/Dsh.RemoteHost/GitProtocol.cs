using System.Text.Json.Serialization;
using Dsh.GitInspect;

namespace Dsh.RemoteHost;

/** Optional Git wire contract; method and capability values are stable. */
public static class GitProtocol
{
    public const string Capability = "git.inspect";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodGitStatus, MethodGitDiff, MethodGitLog, MethodGitWorktreeList, MethodGitWorktreeAdd, MethodGitWorktreeRemove]);

    public const string MethodGitStatus = "host.git.status";
    public const string MethodGitDiff = "host.git.diff";
    public const string MethodGitLog = "host.git.log";
    public const string MethodGitWorktreeList = "host.git.worktree.list";
    public const string MethodGitWorktreeAdd = "host.git.worktree.add";
    public const string MethodGitWorktreeRemove = "host.git.worktree.remove";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteGitRequest))]
[JsonSerializable(typeof(RemoteGitDiffRequest))]
[JsonSerializable(typeof(RemoteGitLogRequest))]
[JsonSerializable(typeof(RemoteGitWorktreeAdd))]
[JsonSerializable(typeof(RemoteGitWorktreeRemove))]
[JsonSerializable(typeof(GitStatus))]
[JsonSerializable(typeof(GitDiff))]
[JsonSerializable(typeof(IReadOnlyList<GitCommitSummary>))]
[JsonSerializable(typeof(GitWorktree))]
[JsonSerializable(typeof(IReadOnlyList<GitWorktree>))]
[JsonSerializable(typeof(bool))]
public partial class GitProtocolJsonContext : JsonSerializerContext;
