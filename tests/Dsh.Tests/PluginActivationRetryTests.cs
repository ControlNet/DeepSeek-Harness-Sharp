using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Tests;

/** Apply 中途失败时已产生的效应(提供的服务等)随之撤销, 重试不会撞上自己上一次留下的注册。 */
public sealed class PluginActivationRetryTests
{
    private const string ServiceName = "retry-probe";

    [Fact]
    public async Task FailedApply_UndoesItsEffects_SoRetryCanActivate()
    {
        var ctx = new Context();
        var attempts = 0;
        var composition = await Composition.StartAsync(ctx,
        [
            new PluginEntry(PluginDefinition.From((pluginCtx, _) =>
            {
                pluginCtx.Provide(ServiceName, new object());
                if (++attempts == 1)
                    throw new InvalidOperationException("first apply fails after providing a service");
                return null;
            }, "@test/retry"), null),
        ]);
        var activation = composition.Find("@test/retry")!;
        Assert.Equal(ActivationState.Failed, activation.State);
        Assert.Null(ctx.Get<object>(ServiceName, false));

        activation.SendInput(new PluginActivationInput(PluginActivationInputKind.Retry));
        await ctx.Root.Scheduler.SettleAsync();

        Assert.Equal(ActivationState.Active, activation.State);
        Assert.Equal(2, attempts);
        Assert.NotNull(ctx.Get<object>(ServiceName, false));
    }
}
