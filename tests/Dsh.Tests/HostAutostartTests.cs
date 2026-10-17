using Dsh.Boot;
using Dsh.Host;

namespace Dsh.Tests;

/** 同一 harness home 的常驻宿主只允许一个注册表 owner。 */
public sealed class HostAutostartTests
{
    [Fact]
    public void InstanceLease_PreventsConcurrentRegistryOwners_AndCanBeReacquired()
    {
        var root = TempTree.CreateDirectory("host-lock");
        try
        {
            var home = new HarnessHome(root);
            using (var first = HostAutostart.TryAcquireInstance(home))
            {
                Assert.NotNull(first);
                Assert.Null(HostAutostart.TryAcquireInstance(home));
            }
            using var next = HostAutostart.TryAcquireInstance(home);
            Assert.NotNull(next);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }
}
