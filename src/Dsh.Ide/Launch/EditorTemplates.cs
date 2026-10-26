using System.Globalization;
using System.Text;
using Dsh.Core;

namespace Dsh.Ide.Launch;

public static class EditorTemplates
{
    private static readonly HashSet<string> Placeholders = ["path", "file", "dir", "workspace", "line", "column", "left", "right"];
    public static void Validate(string template)
    {
        if (template.Contains('\0')) throw new ArgumentException("Editor template contains a null character.");
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '}') throw new ArgumentException("Unmatched template brace.");
            if (template[i] != '{') continue;
            var end = template.IndexOf('}', i + 1);
            if (end < 0 || !Placeholders.Contains(template[(i + 1)..end])) throw new ArgumentException("Unknown editor template placeholder.");
            i = end;
        }
    }
    public static string Render(string template, EditorRequest request, EditorWorkspace workspace)
    {
        Validate(template);
        var target = request.Path ?? request.Right ?? workspace.ProjectPath ?? workspace.Root;
        var output = new StringBuilder(template.Length + target.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') { output.Append(template[i]); continue; }
            var end = template.IndexOf('}', i + 1);
            output.Append(template[(i + 1)..end] switch
            {
                "path" => target,
                "file" => Path.GetFileName(target),
                "dir" => Directory.Exists(target) ? target : Path.GetDirectoryName(target),
                "workspace" => request.Action == EditorAction.OpenWorkspace && request.Path is not null ? request.Path : workspace.ProjectPath ?? workspace.Root,
                "line" => request.Line.ToString(CultureInfo.InvariantCulture),
                "column" => request.Column.ToString(CultureInfo.InvariantCulture),
                "left" => request.Left ?? throw new ArgumentException("Template requires a left path."),
                "right" => request.Right ?? throw new ArgumentException("Template requires a right path."),
                _ => throw new ArgumentException("Unknown editor template placeholder."),
            });
            i = end;
        }
        return output.ToString();
    }
    public static EditorLaunchCommand Create(EditorDescriptor descriptor, EditorInstallation installation, EditorRequest request, EditorWorkspace workspace, EditorOptions options)
    {
        var action = descriptor.Actions[request.Action];
        var args = new List<string>(action.Length + 4);
        var reuse = request.ReuseWindow ?? options.ReuseWindow;
        if (reuse == EditorReuseWindow.Always) args.AddRange(descriptor.ReuseArguments.Select(template => Render(template, request, workspace)));
        if (reuse == EditorReuseWindow.Never) args.AddRange(descriptor.NewWindowArguments.Select(template => Render(template, request, workspace)));
        var focus = request.Focus ?? options.Focus;
        if (focus == EditorFocus.IfRunning) focus = EditorProcess.IsRunning(installation.Executable) ? EditorFocus.Always : EditorFocus.Never;
        if (focus == EditorFocus.Never) args.AddRange(descriptor.BackgroundArguments.Select(template => Render(template, request, workspace)));
        if (focus == EditorFocus.Always) args.AddRange(descriptor.FocusArguments.Select(template => Render(template, request, workspace)));
        if (request.RemoteUri is { } remoteUri)
        {
            if (!SupportsRemoteUri(descriptor, request.Action)) throw new ArgumentException("This editor action does not support remote URI targets.");
            remoteUri = ValidateRemoteUri(remoteUri);
            if (request.Action == EditorAction.OpenAtPosition) args.Add("--goto");
            args.Add(request.Action == EditorAction.OpenWorkspace ? "--folder-uri" : "--file-uri");
            args.Add(request.Action == EditorAction.OpenAtPosition ? $"{remoteUri}:{request.Line}:{request.Column}" : remoteUri);
        }
        else args.AddRange(action.Select(template => Render(template, request, workspace)));
        return new()
        {
            Editor = descriptor.Id, Executable = installation.Executable, Arguments = args.ToArray(),
            WorkingDirectory = descriptor.WorkingDirectory is { } cwd ? Path.GetFullPath(Render(cwd, request, workspace), workspace.Root) : workspace.Root,
            Environment = descriptor.Environment.ToDictionary(p => p.Key, p => Render(p.Value, request, workspace)),
            Kind = descriptor.Kind, Focus = focus, Target = request.Print ? EditorExecutionTarget.Print : request.ExecutionTarget,
            SessionId = request.SessionId, MachineId = request.MachineId, RemoteUri = request.RemoteUri, IsBundle = installation.IsBundle,
        };
    }
    public static string Printable(EditorLaunchCommand command)
    {
        var parts = new[] { command.Executable }.Concat(command.Arguments);
        var invocation = string.Join(" ", parts.Select(part => OperatingSystem.IsWindows() ? QuotePowerShell(part) : QuotePosix(part)));
        if (OperatingSystem.IsWindows())
            return $"Set-Location -LiteralPath {QuotePowerShell(command.WorkingDirectory)}; {string.Join(" ", command.Environment.Select(p => $"[Environment]::SetEnvironmentVariable({QuotePowerShell(p.Key)}, {QuotePowerShell(p.Value)}, 'Process');"))} & {invocation}";
        return $"cd {QuotePosix(command.WorkingDirectory)} && env {string.Join(" ", command.Environment.Select(p => QuotePosix($"{p.Key}={p.Value}")))} {invocation}";
    }
    public static string QuotePosix(string value) => $"'{value.Replace("'", "'\\''")}'";
    public static string QuotePowerShell(string value) => $"'{value.Replace("'", "''")}'";
    public static string QuoteWindowsArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
            else { result.Append('\\', slashes); result.Append(character); }
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }
    public static string QuoteCmdArgument(string value, string percentVariable)
    {
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Batch arguments cannot contain control characters.");
        var quoted = QuoteWindowsArgument(value);
        var result = new StringBuilder(quoted.Length);
        var inQuotes = false;
        foreach (var character in quoted)
        {
            if (character == '"') inQuotes = !inQuotes;
            if (character == '%') { result.Append($"%{percentVariable}%"); continue; }
            if (!inQuotes && character is '&' or '|' or '<' or '>' or '(' or ')' or '^') result.Append('^');
            result.Append(character);
        }
        return result.ToString();
    }
    public static bool SupportsRemoteUri(EditorDescriptor descriptor, EditorAction action) => descriptor.Capabilities.HasFlag(EditorCapability.Remote)
        && (descriptor.Id.Value is "vscode" or "vscode-insiders" or "vscodium" or "cursor" or "windsurf")
        && (action is EditorAction.OpenWorkspace or EditorAction.OpenFile or EditorAction.OpenAtPosition);

    public static string ValidateRemoteUri(string value)
    {
        const string prefix = "vscode-remote://";
        var pathStart = value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value.IndexOf('/', prefix.Length) : -1;
        if (pathStart < 0)
            throw new ArgumentException("Remote editor URI requires a vscode-remote authority and an absolute path.");
        var authority = value[prefix.Length..pathStart];
        var rawPath = value[pathStart..];
        if (authority.Length == 0 || authority.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-' and not '+')
            || !Uri.TryCreate($"https://editor.invalid{rawPath}", UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Remote editor URI must use a configured authority and no query, credentials, or fragment.");
        var path = Uri.UnescapeDataString(rawPath);
        if (!path.StartsWith('/') || path.Contains('\\') || path.Contains("//", StringComparison.Ordinal) || rawPath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Any(char.IsControl) || path.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("Remote editor URI path must be normalized and contain no traversal or control characters.");
        return $"{prefix}{authority}{uri.AbsolutePath}";
    }
}
