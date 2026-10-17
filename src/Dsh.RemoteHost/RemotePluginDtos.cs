namespace Dsh.RemoteHost;

public sealed record RemotePluginInfo(string Package, string Description, bool Enabled, bool ReadAllowed, bool WriteAllowed, bool NetworkAllowed);

public sealed record RemotePluginPermissionRequest(string Package, string Permission, bool Allowed);
