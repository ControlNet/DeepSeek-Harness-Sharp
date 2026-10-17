namespace Dsh.RemoteHost;

/** Semantic names for all known wire capabilities; a host advertises only its installed domains. */
public static class HostProtocolCatalog
{
    public static IReadOnlyDictionary<string, string> KnownCapabilities { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PendingBroadcast"] = HostProtocol.PendingBroadcastCapability,
            ["SessionRegistry"] = HostProtocol.SessionRegistryCapability,
            ["PtyDaemon"] = PtyProtocol.Capability,
            ["PtyResize"] = PtyProtocol.ResizeCapability,
            ["AgentSessions"] = AgentProtocol.Capability,
            ["GitInspect"] = GitProtocol.Capability,
            ["FsTransfer"] = FilesProtocol.Capability,
            ["NotifyHistory"] = NotificationsProtocol.Capability,
            ["SessionEditing"] = SessionEditingProtocol.Capability,
            ["PluginManagement"] = PluginsProtocol.Capability,
            ["ModelCatalog"] = ModelsProtocol.Capability,
        });
}
