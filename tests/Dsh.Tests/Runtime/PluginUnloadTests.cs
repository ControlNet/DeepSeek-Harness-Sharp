using System.Reflection;
using System.Runtime.CompilerServices;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Tests.Runtime;

[Collection(SettingsTemplateTests.RegistryCollection)]
public class PluginUnloadTests
{
    private const int CollectionRounds = 60;

    [Fact]
    public async Task DynamicPlugin_UnloadCollectsLoadContext()
    {
        var ctx = new Context();
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "Dsh.Checkpoints.dll");
        Assert.True(File.Exists(pluginPath), $"plugin assembly not found: {pluginPath}");
        var weak = await LoadActivateUnloadAsync(ctx, pluginPath);
        // The helper's completion can inline this continuation before its live activation frame returns.
        await Task.Yield();
        // 显式完成卸载后，在既有有界 GC 轮数内验证上下文不再被持有；失败保留诊断。
        Assert.True(PluginUnloader.WaitForCollection(weak, out var report, maxRounds: CollectionRounds), report);
    }

    [Fact]
    public async Task DynamicPlugin_UnloadRemovesProvidedServices()
    {
        var ctx = new Context();
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "Dsh.Checkpoints.dll");
        var (activation, provided) = await LoadAndUnloadTrackedAsync(ctx, pluginPath);
        Assert.Equal(ActivationState.Disposed, activation.State);
        foreach (var name in provided)
            Assert.Null(ctx.Get(name, strict: false));
    }

    [Fact]
    public void DynamicPlugin_UnloadingOverlaysPreservesSchemaAndCollectsContexts()
    {
        var (first, second) = UnloadSchemaOverlays();
        Assert.True(PluginUnloader.WaitForCollection(first, out var firstReport, maxRounds: CollectionRounds), firstReport);
        Assert.True(PluginUnloader.WaitForCollection(second, out var secondReport, maxRounds: CollectionRounds), secondReport);
        Assert.Contains(Dsh.Boot.SettingsSchemaCatalog.All(), provider => provider.Package == "@deepseek-ai/dsh-checkpoints");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference First, WeakReference Second) UnloadSchemaOverlays()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Dsh.Checkpoints.dll");
        var first = new PluginHost().TryLoad(path);
        var second = new PluginHost().TryLoad(path);
        try
        {
            var firstWeak = PluginUnloader.Unload(first.Context!);
            Assert.Contains(Dsh.Boot.SettingsSchemaCatalog.All(), provider => provider.Package == "@deepseek-ai/dsh-checkpoints");
            return (firstWeak, PluginUnloader.Unload(second.Context!));
        }
        finally
        {
            _ = PluginUnloader.Unload(first.Context!);
            _ = PluginUnloader.Unload(second.Context!);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadActivateUnloadAsync(Context ctx, string pluginPath)
    {
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        Assert.Contains("@deepseek-ai/dsh-checkpoints", loaded.Packages);
        var definition = host.Catalog.CreateDefinition("@deepseek-ai/dsh-checkpoints");
        var activation = await ctx.Scheduler.AddAsync(definition);
        await ctx.Scheduler.UnloadAsync(activation.Name);
        await ctx.Scheduler.SettleAsync();
        Assert.Equal(ActivationState.Disposed, activation.State);
        host.Catalog.Remove(activation.Name);
        return PluginUnloader.Unload(loaded.Context!);
    }

    [Fact]
    public async Task LeakedReference_IsReportedTruthfully()
    {
        var ctx = new Context();
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "Dsh.Tests.dll");
        var weak = await LoadAndPinAsync(ctx, pluginPath);
        try
        {
            Assert.False(PluginUnloader.WaitForCollection(weak, out var report));
            Assert.Contains("still referenced", report);
        }
        finally
        {
            GC.KeepAlive(_pinnedAssembly);
            _pinnedAssembly = null;
        }
    }

    private static Assembly? _pinnedAssembly;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadAndPinAsync(Context ctx, string pluginPath)
    {
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        var definition = host.Catalog.CreateDefinition("test/local");
        var activation = await ctx.Scheduler.AddAsync(definition);
        await ctx.Scheduler.UnloadAsync(activation.Name);
        host.Catalog.Remove(activation.Name);
        _pinnedAssembly = loaded.Context!.Assemblies.First(candidate => candidate.GetName().Name == "Dsh.Tests");
        return PluginUnloader.Unload(loaded.Context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(PluginActivation Activation, IReadOnlyList<string> Provided)> LoadAndUnloadTrackedAsync(Context ctx, string pluginPath)
    {
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        Assert.Contains("@deepseek-ai/dsh-checkpoints", loaded.Packages);
        var definition = host.Catalog.CreateDefinition("@deepseek-ai/dsh-checkpoints");
        var activation = await ctx.Scheduler.AddAsync(definition);
        var provided = activation.ProvidedNames.ToList();
        await ctx.Scheduler.UnloadAsync(activation.Name);
        host.Catalog.Remove(activation.Name);
        _ = PluginUnloader.Unload(loaded.Context!);
        return (activation, provided);
    }
}
