using Dsh.Core;

namespace Dsh.Ide.Catalog;

public static class BuiltinEditors
{
    public static IReadOnlyList<EditorDescriptor> Create()
    {
        var result = new List<EditorDescriptor>();
        foreach (var (id, command, bundle) in new[] { ("vscode", "code", "Visual Studio Code"), ("vscode-insiders", "code-insiders", "Visual Studio Code - Insiders"), ("vscodium", "codium", "VSCodium"), ("cursor", "cursor", "Cursor"), ("windsurf", "windsurf", "Windsurf") })
            result.Add(Profile(id, command, [".vscode", "*.code-workspace"], [".cs", ".ts", ".tsx", ".js", ".json", ".md", ".py", ".go", ".rs", ".c", ".cpp"], ["--goto", "{path}:{line}:{column}"], ["{workspace}"], [bundle]) with
            {
                Actions = new Dictionary<EditorAction, string[]> { [EditorAction.OpenFile] = ["{path}"], [EditorAction.OpenAtPosition] = ["--goto", "{path}:{line}:{column}"], [EditorAction.OpenWorkspace] = ["{workspace}"], [EditorAction.OpenDiff] = ["--diff", "{left}", "{right}"] },
                Capabilities = EditorCapability.OpenFile | EditorCapability.OpenAtPosition | EditorCapability.OpenWorkspace | EditorCapability.Diff | EditorCapability.History | EditorCapability.Remote,
                ReuseArguments = ["--reuse-window"], NewWindowArguments = ["--new-window"],
            });
        foreach (var (id, command, bundle, extensions) in new[]
        {
            ("rider", "rider", "Rider", new[] { ".cs", ".xaml", ".csproj", ".fs" }),
            ("idea", "idea", "IntelliJ IDEA", new[] { ".java", ".kt", ".scala" }),
            ("pycharm", "pycharm", "PyCharm", new[] { ".py" }),
            ("webstorm", "webstorm", "WebStorm", new[] { ".ts", ".js", ".tsx" }),
            ("clion", "clion", "CLion", new[] { ".c", ".cpp", ".h", ".hpp" }),
            ("goland", "goland", "GoLand", new[] { ".go" }),
            ("rubymine", "rubymine", "RubyMine", new[] { ".rb" }),
            ("phpstorm", "phpstorm", "PhpStorm", new[] { ".php" }),
            ("rustrover", "rustrover", "RustRover", new[] { ".rs" }),
            ("datagrip", "datagrip", "DataGrip", new[] { ".sql" }),
        })
            result.Add(Profile(id, command, [".idea", "*.sln", "*.slnx", "*.iml"], extensions, ["--line", "{line}", "--column", "{column}", "{path}"], ["{workspace}"], [bundle, $"{bundle} CE"]) with
            {
                ExecutableCandidates = [command, $"{command}64.exe", $"{command}.exe", $"{command}.sh"],
                Capabilities = EditorCapability.OpenFile | EditorCapability.OpenAtPosition | EditorCapability.OpenWorkspace | EditorCapability.History,
            });
        result.Add(Profile("zed", "zed", [".zed"], [], ["{path}:{line}:{column}"], ["{workspace}"], ["Zed"]) with { ReuseArguments = ["--reuse"], NewWindowArguments = ["--new"], BackgroundArguments = ["--background"] });
        result.Add(Profile("eclipse", "eclipse", [".project", ".classpath"], [".java"], ["{path}"], ["-data", "{workspace}"], ["Eclipse"]) with { Actions = Basic(["{path}"], ["-data", "{workspace}"]), Capabilities = EditorCapability.OpenFile | EditorCapability.OpenWorkspace });
        result.Add(Profile("codeblocks", "codeblocks", ["*.cbp"], [".c", ".cpp", ".h", ".hpp"], ["{path}"], ["{workspace}"], ["CodeBlocks"]) with { Actions = Basic(["{path}"], ["{workspace}"]), Capabilities = EditorCapability.OpenFile | EditorCapability.OpenWorkspace });
        foreach (var id in new[] { "vim", "nvim" })
            result.Add(Profile(id, id, [], id == "vim" ? [".vim"] : [".vim", ".lua"], ["+call cursor({line},{column})", "{path}"], ["{workspace}"], []) with { Kind = EditorKind.Terminal, Capabilities = EditorCapability.OpenFile | EditorCapability.OpenWorkspace | EditorCapability.OpenAtPosition | EditorCapability.Terminal });
        result.Add(Profile("emacs", "emacsclient", [], [".el"], ["-c", "-a", "", "+{line}:{column}", "{path}"], ["-c", "-a", "", "{workspace}"], ["Emacs"]) with { Kind = EditorKind.Gui, ExecutableCandidates = ["emacsclient", "emacsclient.exe"] });
        result.Add(Profile("emacs-terminal", "emacsclient", [], [".el"], ["-t", "-a", "", "+{line}:{column}", "{path}"], ["-t", "-a", "", "{workspace}"], []) with { Kind = EditorKind.Terminal, Capabilities = EditorCapability.OpenFile | EditorCapability.OpenWorkspace | EditorCapability.OpenAtPosition | EditorCapability.Terminal });
        result.Add(Profile("sublime", "subl", ["*.sublime-project"], [], ["{path}:{line}:{column}"], ["{workspace}"], ["Sublime Text"]) with { ReuseArguments = ["--add"], NewWindowArguments = ["--new-window"], BackgroundArguments = ["--background"] });
        result.Add(Profile("visualstudio", "devenv.exe", ["*.sln", "*.slnx"], [".cs", ".cpp", ".vb", ".fs"], ["/Edit", "{path}"], ["{workspace}"], []) with { Actions = Basic(["/Edit", "{path}"], ["{workspace}"]), Capabilities = EditorCapability.OpenFile | EditorCapability.OpenWorkspace });
        return result;
    }

    private static Dictionary<EditorAction, string[]> Basic(string[] file, string[] workspace) => new() { [EditorAction.OpenFile] = file, [EditorAction.OpenWorkspace] = workspace };
    private static EditorDescriptor Profile(string id, string command, string[] markers, string[] extensions, string[] position, string[] workspace, string[] bundles) => new()
    {
        Id = id, DisplayName = bundles.FirstOrDefault() ?? id, Command = command,
        ExecutableCandidates = [command, $"{command}.exe", $"{command}.cmd"], BundleNames = bundles,
        When = new() { Workspace = markers, Extensions = extensions },
        Actions = new Dictionary<EditorAction, string[]> { [EditorAction.OpenFile] = position, [EditorAction.OpenAtPosition] = position, [EditorAction.OpenWorkspace] = workspace },
        Capabilities = EditorCapability.OpenWorkspace | EditorCapability.OpenFile | EditorCapability.OpenAtPosition,
    };
}
