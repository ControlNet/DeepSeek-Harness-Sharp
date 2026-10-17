using Dsh.Boot;

namespace Dsh.Fleet;

/**
 * The machine list and deployment preferences shared by the GUI, the CLI and the web bridge: the `fleet:` section of settings.yaml.
 * settings.yaml is owner-only, so the SSH passwords of password authentication stay private.
 * Reading never creates settings.yaml; saving rewrites only the fleet section and keeps comments, the other sections and the file's line endings.
 * Only settings.yaml is read or written; configurations from experimental layouts must be added explicitly.
 */
public sealed class MachineStore(HarnessHome home)
{
    public string FilePath => home.SettingsFile;

    public FleetConfig Load()
    {
        return File.Exists(FilePath) && HarnessSettings.Load(home).Fleet is { } fleet
            ? FromSettings(fleet)
            : FleetConfig.Empty;
    }

    /** Settings are re-read right before writing, so a section another client changed in the meantime is not reverted. */
    public void Save(FleetConfig config)
    {
        Validate(config.Machines);
        var settings = HarnessSettings.Load(home);
        settings.Fleet = ToSettings(config);
        settings.Save(home);
    }

    /** Adds a machine, or replaces the one with the same name. */
    public FleetConfig Upsert(MachineSpec machine)
    {
        var config = Load();
        var machines = config.Machines.Where(existing => !string.Equals(existing.Name, machine.Name, StringComparison.Ordinal)).Append(machine).ToList();
        var updated = config with { Machines = machines };
        Save(updated);
        return updated;
    }

    /** Removes a machine; returns false when no machine has that name. */
    public bool Remove(string name)
    {
        var config = Load();
        var machines = config.Machines.Where(existing => !string.Equals(existing.Name, name, StringComparison.Ordinal)).ToList();
        if (machines.Count == config.Machines.Count)
            return false;
        Save(config with { Machines = machines });
        return true;
    }

    private static FleetConfig FromSettings(FleetSettings fleet)
        => new([.. fleet.Machines.Select(FromSettings)], FromSettings(fleet.Deploy));

    private static MachineSpec FromSettings(MachineSettings machine)
        => new(
            machine.Name ?? "",
            machine.Kind ?? MachineKinds.Ssh,
            machine.Host ?? "",
            machine.Port ?? MachineSpec.DefaultSshPort,
            machine.User ?? "",
            machine.Auth ?? SshAuth.Agent,
            machine.KeyPath,
            machine.Proxy,
            machine.RemotePath,
            machine.Password,
            machine.HarnessHome,
            machine.SshOptions);

    private static DeploySettings FromSettings(FleetDeploySettings? deploy)
    {
        var defaults = new DeploySettings();
        if (deploy is null)
            return defaults;
        return new DeploySettings
        {
            AutoDeploy = deploy.AutoDeploy ?? defaults.AutoDeploy,
            ReleaseBaseUrl = deploy.ReleaseBaseUrl ?? defaults.ReleaseBaseUrl,
            ReleaseVersion = deploy.ReleaseVersion ?? defaults.ReleaseVersion,
            Via = deploy.Via ?? defaults.Via,
            ServerProxy = deploy.ServerProxy,
        };
    }

    /** Defaults are left out, so the section only shows what the user chose; an empty list with default preferences removes the section. */
    private static FleetSettings? ToSettings(FleetConfig config)
    {
        if (config.Machines.Count == 0 && config.Deploy == new DeploySettings())
            return null;
        return new FleetSettings { Machines = [.. config.Machines.Select(ToSettings)], Deploy = ToSettings(config.Deploy) };
    }

    private static MachineSettings ToSettings(MachineSpec machine)
    {
        var ssh = !machine.IsLocal;
        return new MachineSettings
        {
            Name = machine.Name,
            Kind = machine.Kind,
            Host = ssh ? machine.Host : null,
            Port = ssh && machine.Port != MachineSpec.DefaultSshPort ? machine.Port : null,
            User = ssh && machine.User.Length > 0 ? machine.User : null,
            Auth = ssh ? machine.Auth : null,
            KeyPath = machine.KeyPath,
            Proxy = machine.Proxy,
            RemotePath = machine.RemotePath,
            Password = machine.Password,
            HarnessHome = machine.HarnessHome,
            SshOptions = machine.SshOptions is { Count: > 0 } options ? [.. options] : null,
        };
    }

    private static FleetDeploySettings? ToSettings(DeploySettings deploy)
    {
        var defaults = new DeploySettings();
        if (deploy == defaults)
            return null;
        return new FleetDeploySettings
        {
            AutoDeploy = deploy.AutoDeploy == defaults.AutoDeploy ? null : deploy.AutoDeploy,
            ReleaseBaseUrl = deploy.ReleaseBaseUrl == defaults.ReleaseBaseUrl ? null : deploy.ReleaseBaseUrl,
            ReleaseVersion = deploy.ReleaseVersion == defaults.ReleaseVersion ? null : deploy.ReleaseVersion,
            Via = deploy.Via == defaults.Via ? null : deploy.Via,
            ServerProxy = deploy.ServerProxy,
        };
    }

    private static void Validate(IReadOnlyList<MachineSpec> machines)
    {
        var duplicate = machines.GroupBy(machine => machine.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"machine name \"{duplicate.Key}\" is used more than once");
        var invalid = machines.FirstOrDefault(machine => string.IsNullOrWhiteSpace(machine.Name) || machine.Name == MachineSpec.LocalName || machine.Name.Contains('/'));
        if (invalid is not null)
            throw new InvalidOperationException($"machine name \"{invalid.Name}\" is reserved or invalid (it must be non-empty, not \"{MachineSpec.LocalName}\", and contain no '/')");
        var incomplete = machines.FirstOrDefault(machine => machine.Kind == MachineKinds.Ssh && string.IsNullOrWhiteSpace(machine.Host));
        if (incomplete is not null)
            throw new InvalidOperationException($"SSH machine \"{incomplete.Name}\" has no host");
    }
}

