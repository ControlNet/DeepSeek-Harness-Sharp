using Dsh.Runtime;

namespace Dsh.Tests.Runtime;

public class RuntimeServiceTests
{
    [Fact]
    public void Provide_RegistersService()
    {
        var ctx = new Context();
        ctx.Provide("foo", 123);
        Assert.Equal(123, ctx.Get("foo"));
    }

    [Fact]
    public void Provide_DuplicateThrows()
    {
        var ctx = new Context();
        ctx.Provide("foo", 123);
        Assert.Throws<RuntimeException>(() => ctx.Provide("foo", 456));
    }

    [Fact]
    public void Provide_DisposeUnregistersService()
    {
        var ctx = new Context();
        var handle = ctx.Provide("foo", 123);
        Assert.Equal(123, ctx.Get("foo"));
        handle.Dispose();
        Assert.Null(ctx.Get("foo"));
    }

    [Fact]
    public async Task Strict_Get_HidesServicesOfInactivePlugins()
    {
        var ctx = new Context();
        (object? Strict, object? Loose) duringApply = default;
        var registration = ctx.Plugin(PluginDefinition.From((pluginCtx, _) =>
        {
            pluginCtx.Provide("late", "value");
            duringApply = (ctx.Get("late"), ctx.Get("late", strict: false));
            throw new InvalidOperationException("activation failed");
        }, "failing"));
        await registration.WaitAsync();
        Assert.Null(duringApply.Strict);
        Assert.Equal("value", duringApply.Loose);
        Assert.Equal(ActivationState.Failed, registration.State);
        Assert.Null(ctx.Get("late", strict: false));
    }

    [Fact]
    public void Service_ClassProvidesItself()
    {
        var ctx = new Context();
        var service = new FooService(ctx);
        Assert.Same(service, ctx.Get<FooService>("foo"));
        Assert.Equal(42, service.Value);
    }

    [Fact]
    public void GetProp_WalksPrototypeChain()
    {
        var ctx = new Context();
        ctx.SetOwn("key", "root");
        var child = ctx.Extend(("other", "child"));
        Assert.Equal("root", child.GetProp("key"));
        Assert.Equal("child", child.GetProp("other"));
    }

    private sealed class FooService(Context ctx) : Service(ctx, "foo")
    {
        public int Value = 42;
    }
}

public class RuntimeLoggerTests
{
    [Fact]
    public void Logger_FormatsPlaceholders()
    {
        var ctx = new Context();
        var logger = ctx.LoggerFor("test");
        logger.Info("hello %s, answer %d", "world", 42);
        var message = Assert.Single(ctx.Root.Logger.Buffer);
        Assert.Equal("test", message.Name);
        Assert.Equal("hello world, answer 42", message.Text);
    }

    [Fact]
    public void Logger_FormatsException()
    {
        var ctx = new Context();
        ctx.LoggerFor("test").Error("%s", new InvalidOperationException("boom"));
        var message = Assert.Single(ctx.Root.Logger.Buffer);
        Assert.Contains("boom", message.Text);
    }

    [Fact]
    public void Logger_BufferIsBounded()
    {
        var ctx = new Context();
        ctx.Root.Logger.BufferSize = 3;
        for (var i = 0; i < 5; i++)
            ctx.LoggerFor("test").Info($"line {i}");
        Assert.Equal(3, ctx.Root.Logger.Buffer.Count);
        Assert.Equal("line 2", ctx.Root.Logger.Buffer[0].Text);
    }
}
