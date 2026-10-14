using Dsh.Boot;
using Dsh.Runtime;

namespace Dsh.Tests;

/** §18 统一持久化路径: 分区解析(默认/相对/绝对/~)与从 ctx 取用。 */
public sealed class DshPathsTests
{
    [Fact]
    public void Resolve_Defaults_Match_Legacy_Layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-paths-root");
        var paths = DshPaths.Default(root);

        Assert.Equal(Path.GetFullPath(root), paths.Root);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "sessions"), paths.SessionsPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "logs"), paths.LogsPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "checkpoints"), paths.CheckpointsPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "attachments"), paths.AttachmentsPath);
        // telemetry 是文件而不是目录(改造前的 <root>/telemetry.jsonl)。
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "telemetry.jsonl"), paths.TelemetryPath);
    }

    [Fact]
    public void Resolve_Honors_Relative_Absolute_And_Home_Overrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-paths-root");
        var absolute = Path.Combine(Path.GetTempPath(), "dsh-paths-absolute");
        var profile = Path.Combine(Path.GetTempPath(), "dsh-paths-profile");
        var paths = DshPaths.Resolve(root, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DshPaths.Sessions] = "sessions-v2",
            [DshPaths.Attachments] = absolute,
            [DshPaths.Cache] = "~/cache-v2",
        }, profile);

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "sessions-v2"), paths.SessionsPath);
        Assert.Equal(Path.GetFullPath(absolute), paths.AttachmentsPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(profile), "cache-v2"), paths.CachePath);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "logs"), paths.LogsPath);
    }

    [Fact]
    public void FromContext_Prefers_Prop_And_Falls_Back_To_Home()
    {
        var ctx = new Context();
        Assert.Null(DshPaths.FromContext(ctx));

        // 只有 dshHomePath(独立组合/旧测试)时按默认分区兜底。
        ctx.SetOwn("dshHomePath", Path.Combine(Path.GetTempPath(), "dsh-paths-fallback"));
        var fallback = DshPaths.FromContext(ctx);
        Assert.NotNull(fallback);
        Assert.Equal(Path.Combine(fallback!.Root, "sessions"), fallback.SessionsPath);

        // 写入 dshPaths 后以它为准(可配置分区)。
        ctx.SetOwn(DshPaths.PropName, DshPaths.Resolve(fallback.Root, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DshPaths.Sessions] = "sessions-v3",
        }));
        Assert.Equal(Path.Combine(fallback.Root, "sessions-v3"), DshPaths.FromContext(ctx)!.SessionsPath);
    }
}
