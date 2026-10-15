using Dsh.Runtime;
using Dsh.Runtime.Ioc;

namespace Dsh.Tests.Runtime;

public class IocTests
{
    [Fact]
    public void RegisterInstance_IsKeyedByNameAndIsolate()
    {
        using var registry = new DryIocServiceRegistry();
        var root = new Probe("root");
        var isolated = new Probe("isolated");
        registry.Register("svc", root);
        registry.Register("svc", isolated, isolateKey: "iso");

        Assert.Same(root, registry.Resolve("svc"));
        Assert.Same(isolated, registry.Resolve("svc", "iso"));
        Assert.Null(registry.Resolve("svc", "missing-isolate"));
        Assert.Null(registry.Resolve("missing-name"));
    }

    [Fact]
    public void Register_SameKeyReplacesInstance()
    {
        using var registry = new DryIocServiceRegistry();
        registry.Register("svc", new Probe("first"));
        var second = new Probe("second");
        registry.Register("svc", second);

        Assert.Same(second, registry.Resolve("svc"));
    }

    [Fact]
    public void Unregister_RemovesInstanceAndReportsExistence()
    {
        using var registry = new DryIocServiceRegistry();
        registry.Register("svc", new Probe("value"), isolateKey: "iso");

        Assert.True(registry.Unregister("svc", "iso"));
        Assert.Null(registry.Resolve("svc", "iso"));
        Assert.False(registry.Unregister("svc", "iso"));
    }

    [Fact]
    public void ResolveMany_ReturnsEveryIsolate()
    {
        using var registry = new DryIocServiceRegistry();
        registry.Register("svc", new Probe("a"));
        registry.Register("svc", new Probe("b"), isolateKey: "iso-1");
        registry.Register("svc", new Probe("c"), isolateKey: "iso-2");

        var names = registry.ResolveMany("svc").OfType<Probe>().Select(probe => probe.Name).ToList();

        Assert.Equal(["a", "b", "c"], names);
        Assert.Empty(registry.ResolveMany("missing"));
    }

    [Fact]
    public void FactoryLifetimes_FollowSingletonTransientScoped()
    {
        using var registry = new DryIocServiceRegistry();
        registry.RegisterFactory<Probe>("singleton", _ => new Probe("s"), ServiceLifetime.Singleton);
        registry.RegisterFactory<Probe>("transient", _ => new Probe("t"), ServiceLifetime.Transient);
        registry.RegisterFactory<Probe>("scoped", _ => new Probe("sc"), ServiceLifetime.Scoped);

        Assert.Same(registry.Resolve("singleton"), registry.Resolve("singleton"));
        Assert.NotSame(registry.Resolve("transient"), registry.Resolve("transient"));

        using var scope1 = registry.OpenScope("scope-1");
        using var scope2 = registry.OpenScope("scope-2");
        Assert.Same(scope1.Resolve("scoped"), scope1.Resolve("scoped"));
        Assert.NotSame(scope1.Resolve("scoped"), scope2.Resolve("scoped"));
    }

    [Fact]
    public void Factory_ReceivesResolverForDependencies()
    {
        using var registry = new DryIocServiceRegistry();
        registry.Register("dependency", "value");
        registry.RegisterFactory<Probe>("dependent", resolver => new Probe((string)resolver.Resolve("dependency")!), ServiceLifetime.Transient);

        Assert.Equal("value", ((Probe)registry.Resolve("dependent")!).Name);
    }

    [Fact]
    public void ScopedService_IsUnavailableAtRootButResolvableInScope()
    {
        using var registry = new DryIocServiceRegistry();
        registry.RegisterFactory<Probe>("scoped", _ => new Probe("sc"), ServiceLifetime.Scoped);

        Assert.Null(registry.Resolve("scoped"));
        using var scope = registry.OpenScope("scope");
        Assert.NotNull(scope.Resolve("scoped"));
    }

    [Fact]
    public void Context_ProvideAndDispose_GoThroughRegistry()
    {
        using var registry = new DryIocServiceRegistry();
        using var logging = Dsh.Runtime.Logging.LoggingSetup.Create();
        var ctx = new Context(logging, registry);
        var probe = new Probe("p");
        var handle = ctx.Provide("probe", probe);

        Assert.Same(probe, ctx.Get("probe"));
        Assert.Same(probe, ctx.ServiceProvider.GetService(typeof(Probe)));
        Assert.Null(ctx.ServiceProvider.GetService(typeof(string)));

        handle.Dispose();

        Assert.Null(ctx.Get("probe"));
        Assert.Null(ctx.ServiceProvider.GetService(typeof(Probe)));
    }

    [Fact]
    public async Task Context_StrictGate_HidesServiceOfInactiveOwner()
    {
        using var registry = new DryIocServiceRegistry();
        using var logging = Dsh.Runtime.Logging.LoggingSetup.Create();
        var ctx = new Context(logging, registry);
        var probe = new Probe("p");
        (object? Strict, object? Loose) duringApply = default;
        var activation = ctx.Plugin(PluginDefinition.From((pluginCtx, _) =>
        {
            pluginCtx.Provide("late", probe);
            duringApply = (ctx.Get("late"), ctx.Get("late", strict: false));
            throw new InvalidOperationException("activation failed");
        }, "failing"));
        await activation.WaitAsync();

        Assert.Null(duringApply.Strict);
        Assert.Same(probe, duringApply.Loose);
        Assert.Equal(ActivationState.Failed, activation.State);
        Assert.Null(ctx.Get("late", strict: false));
    }

    private sealed class Probe(string name)
    {
        public string Name { get; } = name;
    }
}
