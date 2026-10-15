using System.Text.Json.Serialization;

namespace Dsh.GitInspect;

/** Source-generated metadata for the GitInspect results, so remote protocols can carry them under trimming and NativeAOT. */
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GitStatus))]
[JsonSerializable(typeof(GitDiff))]
[JsonSerializable(typeof(GitDiffTarget))]
[JsonSerializable(typeof(GitDiffLimits))]
[JsonSerializable(typeof(IReadOnlyList<GitCommitSummary>))]
[JsonSerializable(typeof(IReadOnlyList<GitWorktree>))]
[JsonSerializable(typeof(GitWorktree))]
[JsonSerializable(typeof(GitWorktreeAddRequest))]
public sealed partial class GitInspectJsonContext : JsonSerializerContext;
