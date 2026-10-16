using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Gui.Services;

/** settings.yaml 的唯一读写入口: 有对应命令时复用命令的校验与副作用, 无命令时才直接读写。 */
public sealed class SettingsFacade(Context ctx, HarnessHome home)
{
    public event Action? Changed;

    public string SettingsPath => Path.Combine(home.Root, "settings.yaml");

    public HarnessSettings Load() => HarnessSettings.Load(home);

    public void UpdateSafety(bool autoApprove, IReadOnlyList<string> blacklist)
    {
        var settings = Load();
        settings.Safety = new SafetySettings
        {
            AutoApprove = autoApprove,
            Blacklist = [.. blacklist],
        };
        settings.Save(home);
        Changed?.Invoke();
    }

    /** 某个插件的参数段(例如 GUI 自己的外观/显卡设置); 合并已有键后写回。 */
    public void SavePluginParameters(string package, IReadOnlyDictionary<string, object?> parameters)
    {
        var settings = Load();
        var existing = settings.Plugins.GetValueOrDefault(package);
        var merged = new Dictionary<string, object?>(existing?.Parameters ?? [], StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
            merged[key] = value;
        settings.Plugins[package] = (existing ?? new PluginSetting()) with { Parameters = merged };
        settings.SavePlugins(home);
        Changed?.Invoke();
    }

    public async Task<string> RunCommandAsync(IAgent agent, string line)
        => await new CommandBridge(ctx).RunAsync(agent, line);
}
