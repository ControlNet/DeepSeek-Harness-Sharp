using System.Reflection;
using System.Xml.Linq;
using Dsh.Plugins;

namespace Dsh.Tests;

/**
 * 模块引用白名单: src 下的项目默认只能引用共享层, 插件之间禁止互相引用, 协作一律经 ctx 服务 + Core/Interaction 契约。
 * 宿主(Dsh.Host 与两个 exe)不在 src 下, 是唯一允许"知道全部"的组合根。
 */
public sealed class PluginReferenceRulesTests
{
    private static readonly HashSet<string> HostInfraModules = ["Dsh.Plugins.Host", "Dsh.Plugins.Native"];

    private static readonly Dictionary<string, HashSet<string>> HostInfraAllowedConsumers = new()
    {
        ["Dsh.Plugins.Host"] = ["Dsh.Boot", "Dsh.Plugins.Native.Host"],
        ["Dsh.Plugins.Native"] = ["Dsh.Plugins.Host", "Dsh.Plugins.Native.Host"],
    };

    /** 插件装载机制本身: 只受 HostInfra 规则约束(对 HostInfra 的引用也只由该规则裁定)。 */
    private static readonly HashSet<string> LoaderModules =
        ["Dsh.Plugins.Host", "Dsh.Plugins.Native", "Dsh.Plugins.Native.Host", "Dsh.Plugins.Generator"];

    /** 契约层: 任何项目都可直接引用(Boot 按用户决策承载 settings 模型)。 */
    private static readonly HashSet<string> ContractLayers =
        ["Dsh.Runtime", "Dsh.Plugins.Abstractions", "Dsh.Llm", "Dsh.Core", "Dsh.Interaction", "Dsh.Boot"];

    /** 共享库: 不含插件入口的纯能力库, 可被多个插件复用。 */
    private static readonly HashSet<string> SharedLibraries =
        ["Dsh.Pty", "Dsh.Transport", "Dsh.RemoteHost", "Dsh.Sdk", "Dsh.ScriptHost", "Dsh.GitInspect", "Dsh.Notifications", "Dsh.AgentAdapters", "Dsh.Fleet.Client"];

    /** 卫星程序集: 实现所属插件自有的 SPI 并由其反射发现, 只允许引用所属插件。 */
    private static readonly Dictionary<string, HashSet<string>> SatelliteOwners = new()
    {
        ["Dsh.Tui.GL"] = ["Dsh.Tui"],
        ["Dsh.Tui.Vk"] = ["Dsh.Tui"],
    };

    private static readonly Dictionary<string, HashSet<string>> LayerAllowed = new()
    {
        ["Dsh.Runtime"] = [],
        ["Dsh.Pty"] = ["Dsh.Transport"],
        ["Dsh.Transport"] = [],
        ["Dsh.Plugins.Native"] = [],
        ["Dsh.Plugins.Abstractions"] = ["Dsh.Runtime"],
        ["Dsh.Llm"] = ["Dsh.Runtime"],
        ["Dsh.Boot"] = ["Dsh.Runtime", "Dsh.Plugins.Host"],
        ["Dsh.Core"] = ["Dsh.Runtime", "Dsh.Plugins.Abstractions", "Dsh.Llm"],
        ["Dsh.Interaction"] = ["Dsh.Runtime", "Dsh.Plugins.Abstractions", "Dsh.Llm", "Dsh.Core", "Dsh.Boot"],
    };

    [Fact]
    public void HostInfra_ReferencedOnlyByPluginLoaders()
    {
        foreach (var (project, references) in ProjectReferences())
        {
            var illegal = references
                .Where(HostInfraModules.Contains)
                .Where(infra => !HostInfraAllowedConsumers.TryGetValue(infra, out var allowed) || !allowed.Contains(project))
                .ToList();
            Assert.True(illegal.Count == 0, $"{project} must not reference: {string.Join(", ", illegal)}");
        }
    }

    [Fact]
    public void Projects_ReferenceOnlySharedLayers()
    {
        var projects = ProjectReferences();
        var violations = new List<string>();
        foreach (var (project, references) in projects.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (LoaderModules.Contains(project))
                continue;
            var owners = SatelliteOwners.GetValueOrDefault(project) ?? [];
            violations.AddRange(references
                .Where(target => !HostInfraModules.Contains(target))
                .Where(target => !ContractLayers.Contains(target) && !SharedLibraries.Contains(target) && !owners.Contains(target))
                .Order(StringComparer.Ordinal)
                .Select(target => $"{project} -> {target}"));
        }
        Assert.True(violations.Count == 0, $"plugin-to-plugin references:\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void Whitelists_NameExistingProjects()
    {
        var projects = ProjectReferences();
        var stale = LoaderModules
            .Concat(ContractLayers)
            .Concat(SharedLibraries)
            .Concat(SatelliteOwners.Keys)
            .Concat(SatelliteOwners.Values.SelectMany(owners => owners))
            .Concat(LayerAllowed.Keys)
            .Where(name => !projects.ContainsKey(name))
            .Distinct()
            .ToList();
        Assert.True(stale.Count == 0, $"whitelisted projects not found under src: {string.Join(", ", stale)}");
    }

    [Fact]
    public void SharedLibrariesAndSatellites_AreNotPlugins()
    {
        var plugins = SharedLibraries
            .Concat(SatelliteOwners.Keys)
            .Where(name => Assembly.Load(name).GetType(PluginHost.ManifestTypeName) is not null)
            .ToList();
        Assert.True(plugins.Count == 0, $"shared libraries and satellites must not declare a plugin: {string.Join(", ", plugins)}");
    }

    [Fact]
    public void CapabilityModules_DeclarePluginManifests()
    {
        var modules = ProjectReferences().Keys
            .Where(name => !LoaderModules.Contains(name) && !ContractLayers.Contains(name)
                && !SharedLibraries.Contains(name) && !SatelliteOwners.ContainsKey(name));
        var missing = modules
            .Where(name => Assembly.Load(name).GetType(PluginHost.ManifestTypeName) is null)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0, $"capability modules must declare a plugin: {string.Join(", ", missing)}");
    }

    [Fact]
    public void ContractLayers_DoNotReferenceUpwards()
    {
        var projects = ProjectReferences();
        foreach (var (project, allowed) in LayerAllowed)
        {
            var illegal = projects[project].Where(target => !allowed.Contains(target)).ToList();
            Assert.True(illegal.Count == 0, $"{project} must only reference [{string.Join(", ", allowed)}], found: {string.Join(", ", illegal)}");
        }
    }

    private static Dictionary<string, HashSet<string>> ProjectReferences()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var csproj in Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories))
        {
            var project = Path.GetFileNameWithoutExtension(csproj);
            var references = XDocument.Load(csproj)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .OfType<string>()
                .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
                .ToHashSet(StringComparer.Ordinal);
            result[project] = references;
        }
        return result;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeepSeek-Harness-Sharp.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found from test assembly location");
    }
}
