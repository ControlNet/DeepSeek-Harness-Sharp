namespace Dsh.Plugins;

/** One-release package-name migration; aliases never create a second catalog entry. */
public static class PluginPackageNames
{
    public const string Ide = "@deepseek-ai/dsh-ide";
    public const string LegacyIdeHistory = "@deepseek-ai/dsh-ide-history";

    public static string Canonical(string package)
        => package == LegacyIdeHistory ? Ide : package;
}
