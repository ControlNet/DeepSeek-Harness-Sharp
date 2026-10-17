using Dsh.Boot;
using Dsh.Pty;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

public sealed class HarnessStorageTests
{
    [Fact]
    public async Task ApplyStorageRoot_MovesExistingDataDirectories()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var home = new HarnessHome(Path.Combine(dir, "home"));
            home.Ensure();
            var target = Path.Combine(dir, "data");
            await File.WriteAllTextAsync(home.SettingsFile,
                $"global_default_model: p/m\nstorage:\n  root: {target.Replace('\\', '/')}\n",
                TestContext.Current.CancellationToken);
            Directory.CreateDirectory(home.SessionsPath);
            await File.WriteAllTextAsync(Path.Combine(home.SessionsPath, "s.jsonl"), "x",
                TestContext.Current.CancellationToken);
            var settings = HarnessSettings.Load(home);

            var resolved = HarnessStorage.ApplyStorageRoot(home, settings);

            Assert.Equal(Path.GetFullPath(target), resolved.Root);
            Assert.True(File.Exists(resolved.SettingsFile));
            var moved = Path.Combine(resolved.SessionsPath, "s.jsonl");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(moved) && DateTime.UtcNow < deadline)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(moved));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void ApplyStorageRoot_KeepsHomeWhenRootUnset()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var home = new HarnessHome(Path.Combine(dir, "home"));
            home.Ensure();
            var settings = HarnessSettings.Load(home);

            var resolved = HarnessStorage.ApplyStorageRoot(home, settings);

            Assert.Equal(home.Root, resolved.Root);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /** PTY daemon 与远端 host 的端点根都要跟随 storage.root 写下的记录, 否则 socket 落在默认根而客户端去 storage.root 找。 */
    [Fact]
    public async Task LocalEndpointRoot_FollowsTheStorageRootMarker()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-storage-{Guid.NewGuid():N}");
        var home = Path.Combine(dir, "home");
        var target = Path.Combine(dir, "data");
        Directory.CreateDirectory(home);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(home, HarnessHome.StorageRootMarker), $"{target}\n", TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(target), LocalEndpointRoot.FollowMarker(home));
            Assert.Equal(Path.GetFullPath(target), LocalEndpointRoot.FollowMarker(Path.Combine(home, ".")));
            Assert.Equal(Path.GetFullPath(target + "-unmarked"), LocalEndpointRoot.FollowMarker(target + "-unmarked"));
            Assert.Equal(PtyDaemonPaths.DefaultRoot(), RemoteHostEndpoint.DefaultRoot());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Resolve_PrefersExplicitRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-storage-{Guid.NewGuid():N}");
        Assert.Equal(Path.GetFullPath(dir), HarnessHome.Resolve(dir).Root);
    }

    /** §18: storage 的分区键要生效, 未配置的仍是改造前的默认布局(telemetry 是文件)。 */
    [Fact]
    public async Task Paths_HonorConfiguredPartitions()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var home = new HarnessHome(Path.Combine(dir, "home"));
            home.Ensure();
            await File.WriteAllTextAsync(home.SettingsFile,
                "storage:\n  sessions: sessions-v2\n  cache: cache-v2\n",
                TestContext.Current.CancellationToken);
            var paths = HarnessStorage.Paths(home, HarnessSettings.Load(home));

            Assert.Equal(Path.Combine(home.Root, "sessions-v2"), paths.SessionsPath);
            Assert.Equal(Path.Combine(home.Root, "cache-v2"), paths.CachePath);
            Assert.Equal(Path.Combine(home.Root, "telemetry.jsonl"), paths.TelemetryPath);
            Assert.Equal(Path.Combine(home.Root, "tool-results"), paths.ToolResultsPath);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /** §18/D14: 手动迁移复用同一实现 —— 分区搬走, 旧根归档成 <old>.migrated-<ts>, 不直接删。 */
    [Fact]
    public async Task Migrate_MovesPartitionsAndArchivesOldRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var from = new HarnessHome(Path.Combine(dir, "home"));
            var to = new HarnessHome(Path.Combine(dir, "data"));
            Directory.CreateDirectory(from.SessionsPath);
            await File.WriteAllTextAsync(Path.Combine(from.SessionsPath, "s.jsonl"), "x",
                TestContext.Current.CancellationToken);

            var results = HarnessStorage.Migrate(from, to);

            Assert.Contains(results, result => result.Directory == "sessions" && result.Outcome == "moved");
            Assert.Equal("x", await File.ReadAllTextAsync(
                Path.Combine(to.SessionsPath, "s.jsonl"), TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(from.Root), "旧根应被归档而不是留在原地");
            Assert.Single(Directory.GetDirectories(dir, "home.migrated-*"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
