using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Boot;
using Dsh.Transport;

namespace Dsh.Fleet;

/**
 * The machine list and deployment preferences shared by the GUI and the CLI: `<harness home>/fleet/machines.json`.
 * The file holds SSH passwords when password authentication is used, so it is written owner-only.
 * When the file does not exist yet, the remote workspaces that older GUI builds kept in settings.yaml are imported once and removed from there, so there is only ever one list.
 */
public sealed class MachineStore(HarnessHome home)
{
    public const string DirectoryName = "fleet";
    public const string FileName = "machines.json";

    public string FilePath => Path.Combine(home.SubPath(DirectoryName), FileName);

    public FleetConfig Load()
    {
        if (File.Exists(FilePath))
            return Parse(File.ReadAllText(FilePath));
        if (LegacyGuiWorkspaces.Read(home) is not { } legacy)
            return FleetConfig.Empty;
        Save(legacy);
        LegacyGuiWorkspaces.Remove(home);
        return legacy;
    }

    public void Save(FleetConfig config)
    {
        Validate(config.Machines);
        OwnerOnlyFiles.EnsureDirectory(home.SubPath(DirectoryName));
        var document = new MachineStoreDocument([.. config.Machines], config.Deploy);
        OwnerOnlyFiles.Write(FilePath, JsonSerializer.Serialize(document, FleetJsonContext.Default.MachineStoreDocument));
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

    private static FleetConfig Parse(string json)
    {
        var document = JsonSerializer.Deserialize(json, FleetJsonContext.Default.MachineStoreDocument);
        return document is null ? FleetConfig.Empty : new FleetConfig(document.Machines ?? [], document.Deploy ?? new DeploySettings());
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

internal sealed record MachineStoreDocument(List<MachineSpec>? Machines, DeploySettings? Deploy);

/** Source-generated JSON for the machine list and for reading event envelopes (AOT and trimming safe). */
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MachineStoreDocument))]
[JsonSerializable(typeof(EventEnvelope))]
internal sealed partial class FleetJsonContext : JsonSerializerContext;

/** The part of a session event the client needs to resume a subscription. */
internal sealed record EventEnvelope(long Seq);
