using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Dsh.Core;
using Microsoft.Win32;

namespace Dsh.Ide.Catalog;

public sealed class EditorDetector
{
    public EditorInstallation? Detect(EditorDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        foreach (var (path, source, bundle) in Candidates(descriptor))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bundle ? Directory.Exists(path) : IsExecutable(path))
                return new(descriptor.Id, Path.GetFullPath(path), source, ReadVersion(path, bundle), bundle);
        }
        return null;
    }
    public static string? FindOnPath(string command)
    {
        if (Path.IsPathRooted(command)) return IsExecutable(command) ? command : null;
        var suffixes = OperatingSystem.IsWindows() && !Path.HasExtension(command) ? new[] { "", ".exe", ".cmd", ".bat", ".com" } : new[] { "" };
        foreach (var root in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(root.Trim('"'), $"{command}{suffix}");
                if (IsExecutable(candidate)) return Path.GetFullPath(candidate);
            }
        return null;
    }
    private static IEnumerable<(string Path, string Source, bool Bundle)> Candidates(EditorDescriptor d)
    {
        if (Path.IsPathRooted(d.Command)) { yield return (d.Command, "configuration", false); yield break; }
        foreach (var command in new[] { d.Command }.Concat(d.ExecutableCandidates).Distinct(StringComparer.OrdinalIgnoreCase))
            if (FindOnPath(command) is { } found) yield return (found, "PATH", false);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var names = new[] { d.Command }.Concat(d.ExecutableCandidates).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var roots = OperatingSystem.IsWindows() ? WindowsRoots(d) : new[] { "/usr/bin", "/usr/local/bin", "/snap/bin", $"/opt/{d.Id.Value}", $"/opt/{d.Id.Value}/bin", $"/opt/jetbrains/{d.Id.Value}/bin", Path.Combine(home, ".local", "bin"), Path.Combine(home, ".local", "share", "JetBrains", "Toolbox", "scripts"), Path.Combine(home, "Library", "Application Support", "JetBrains", "Toolbox", "scripts") };
        foreach (var root in roots)
            foreach (var name in names) yield return (Path.Combine(root, name), "known-location", false);
        if (OperatingSystem.IsWindows())
        {
            foreach (var path in RegistryExecutables(names)) yield return (path, "registry", false);
            if (d.Id.Value == "visualstudio" && VsWhere() is { } visualStudio) yield return (visualStudio, "vswhere", false);
        }
        if (OperatingSystem.IsMacOS())
            foreach (var root in new[] { "/Applications", Path.Combine(home, "Applications") })
                foreach (var name in d.BundleNames)
                {
                    var bundle = Path.Combine(root, $"{name}.app");
                    var cli = Path.Combine(bundle, "Contents", "Resources", "app", "bin", d.Command);
                    yield return (cli, "application-bundle-cli", false);
                    yield return (Path.Combine(bundle, "Contents", "MacOS", d.Command), "application-bundle-cli", false);
                    yield return (bundle, "application-bundle", true);
                }
        if (OperatingSystem.IsLinux())
            foreach (var root in new[] { "/usr/share/applications", "/usr/local/share/applications", Path.Combine(home, ".local", "share", "applications") })
                foreach (var name in names.Append(d.Id.Value).Select(Path.GetFileNameWithoutExtension).Distinct())
                {
                    var entry = Path.Combine(root, $"{name}.desktop");
                    if (ReadDesktopExecutable(entry) is { } executable) yield return (executable, "desktop-entry", false);
                }
        foreach (var product in ToolboxDirectories(home, d.Id.Value))
            foreach (var name in names) yield return (Path.Combine(product, "bin", name), "Toolbox", false);
    }
    private static IEnumerable<string> ToolboxDirectories(string home, string id)
    {
        var root = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetBrains", "Toolbox", "apps") : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "JetBrains", "Toolbox", "apps") : Path.Combine(home, ".local", "share", "JetBrains", "Toolbox", "apps");
        if (!Directory.Exists(root)) yield break;
        // Toolbox uses both product/version and product/channel/version layouts; never scan arbitrary disks.
        foreach (var product in Directory.EnumerateDirectories(root).Where(p => Path.GetFileName(p).Contains(id, StringComparison.OrdinalIgnoreCase)))
        {
            yield return product;
            foreach (var first in Directory.EnumerateDirectories(product).OrderDescending())
            {
                yield return first;
                foreach (var second in Directory.EnumerateDirectories(first).OrderDescending()) yield return second;
            }
        }
    }
    private static IEnumerable<string> WindowsRoots(EditorDescriptor d)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { Path.Combine(local, "Programs", d.DisplayName, "bin"), Path.Combine(local, "Programs", d.DisplayName), Path.Combine(local, "JetBrains", "Toolbox", "scripts"), Path.Combine(programFiles, d.DisplayName), Path.Combine(x86, d.DisplayName), Path.Combine(programFiles, "CodeBlocks"), Path.Combine(programFiles, "Eclipse"), Path.Combine(programFiles, "Neovim", "bin"), Path.Combine(programFiles, "Vim", "vim91"), Path.Combine(programFiles, "Sublime Text"), Path.Combine(local, "Programs", "Microsoft VS Code", "bin"), Path.Combine(local, "Programs", "Microsoft VS Code Insiders", "bin"), Path.Combine(programFiles, "Microsoft VS Code", "bin") })
            yield return root;
        var jetbrains = Path.Combine(programFiles, "JetBrains");
        if (Directory.Exists(jetbrains))
            foreach (var product in Directory.EnumerateDirectories(jetbrains).Where(p => Path.GetFileName(p).StartsWith(d.DisplayName, StringComparison.OrdinalIgnoreCase)).OrderDescending())
                yield return Path.Combine(product, "bin");
        foreach (var root in new[] { Path.Combine(programFiles, "Vim"), Path.Combine(programFiles, "Emacs") })
            if (Directory.Exists(root))
                foreach (var product in Directory.EnumerateDirectories(root).OrderDescending())
                    yield return Path.Combine(product, d.Id.Value is "vim" ? "" : "bin");
    }
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryExecutables(string[] names)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var name in names)
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}");
                if (key?.GetValue(null) is string value) yield return value.Trim('"');
            }
    }
    [SupportedOSPlatform("windows")]
    private static string? VsWhere()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(executable)) return null;
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true } };
        foreach (var arg in new[] { "-latest", "-products", "*", "-property", "productPath" }) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000)) { process.Kill(true); process.WaitForExit(); return null; }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
    private static string? ReadDesktopExecutable(string entry)
    {
        if (!File.Exists(entry)) return null;
        var line = File.ReadLines(entry).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        if (line is null) return null;
        var value = line[5..].Trim();
        var command = value.StartsWith('"') ? value[1..].Split('"')[0] : value.Split(' ', 2)[0];
        return FindOnPath(command);
    }
    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
    private static string? ReadVersion(string path, bool bundle)
    {
        if (bundle)
        {
            var plist = Path.Combine(path, "Contents", "Info.plist");
            if (!File.Exists(plist)) return null;
            try
            {
                var document = XDocument.Load(plist);
                return document.Descendants("key").FirstOrDefault(k => k.Value == "CFBundleShortVersionString")?.ElementsAfterSelf("string").FirstOrDefault()?.Value;
            }
            catch (System.Xml.XmlException) { return null; }
        }
        return OperatingSystem.IsWindows() ? FileVersionInfo.GetVersionInfo(path).ProductVersion : null;
    }
}
