using Dsh.Boot;

namespace Dsh.Fleet;

/**
 * One-time import, into the fleet section of settings.yaml, of the SSH remote workspaces that older GUI builds kept under plugins."@deepseek-ai/dsh-gui".
 * Reading never creates settings.yaml; removal drops only the keys listed here and leaves the rest of the GUI parameters untouched.
 */
internal static class LegacyGuiWorkspaces
{
    private const string GuiPackage = "@deepseek-ai/dsh-gui";
    private const string WorkspacesKey = "remoteWorkspaces";
    private const string AutoDeployKey = "remoteAutoDeploy";
    private const string ReleaseBaseUrlKey = "remoteReleaseBaseUrl";
    private const string ReleaseVersionKey = "remoteReleaseVersion";
    private const string DownloadProxyKey = "remoteDownloadProxy";
    private const string DownloadViaKey = "remoteDownloadVia";
    private const string ServerProxyKey = "remoteServerProxy";

    private static readonly string[] Keys = [WorkspacesKey, AutoDeployKey, ReleaseBaseUrlKey, ReleaseVersionKey, DownloadProxyKey, DownloadViaKey, ServerProxyKey];

    public static FleetConfig? Read(HarnessHome home)
    {
        if (!File.Exists(home.SettingsFile))
            return null;
        var parameters = HarnessSettings.Load(home).Plugins.GetValueOrDefault(GuiPackage)?.Parameters;
        if (parameters is null || !Keys.Any(parameters.ContainsKey))
            return null;
        return new FleetConfig(Machines(parameters), Deploy(parameters));
    }

    public static void Remove(HarnessHome home)
    {
        var settings = HarnessSettings.Load(home);
        if (settings.Plugins.GetValueOrDefault(GuiPackage) is not { } gui)
            return;
        foreach (var key in Keys)
            gui.Parameters.Remove(key);
        settings.SavePlugins(home);
    }

    private static DeploySettings Deploy(IReadOnlyDictionary<string, object?> parameters)
    {
        var defaults = new DeploySettings();
        return new DeploySettings
        {
            AutoDeploy = parameters.GetValueOrDefault(AutoDeployKey) as bool? ?? defaults.AutoDeploy,
            ReleaseBaseUrl = Text(parameters, ReleaseBaseUrlKey) ?? defaults.ReleaseBaseUrl,
            ReleaseVersion = Text(parameters, ReleaseVersionKey) ?? defaults.ReleaseVersion,
            DownloadProxy = Text(parameters, DownloadProxyKey),
            Via = Text(parameters, DownloadViaKey) ?? defaults.Via,
            ServerProxy = Text(parameters, ServerProxyKey),
        };
    }

    private static List<MachineSpec> Machines(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.GetValueOrDefault(WorkspacesKey) is not IEnumerable<object?> items)
            return [];
        var machines = new List<MachineSpec>();
        foreach (var map in items.OfType<IReadOnlyDictionary<string, object?>>())
        {
            if (Text(map, "name") is not { } name || Text(map, "host") is not { } host)
                continue;
            machines.Add(new MachineSpec(
                name,
                MachineKinds.Ssh,
                host,
                Port(map),
                Text(map, "user") ?? "",
                Text(map, "auth") ?? SshAuth.Agent,
                Text(map, "keyPath"),
                Text(map, "proxy"),
                Text(map, "remotePath"),
                Text(map, "password")));
        }

        return machines;
    }

    private static int Port(IReadOnlyDictionary<string, object?> map) => map.GetValueOrDefault("port") switch
    {
        int value => value,
        long value => (int)value,
        double value => (int)value,
        _ => MachineSpec.DefaultSshPort,
    };

    private static string? Text(IReadOnlyDictionary<string, object?> map, string key)
        => map.GetValueOrDefault(key) is string text && text.Length > 0 ? text : null;
}
