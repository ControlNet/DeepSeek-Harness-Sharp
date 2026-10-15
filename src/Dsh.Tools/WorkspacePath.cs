using System.Text.Json;
using Dsh.Core;

namespace Dsh.Tools;

internal static class WorkspacePath
{
    public static string Resolve(ToolRunContext exec, string path)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        var sessionCwd = exec.Agent?.Session.WorkingDirectory;
        return Path.GetFullPath(sessionCwd is not null ? Path.Combine(sessionCwd, path) : path);
    }

    /** The resolved path in a string argument as a write target; malformed arguments yield none and are rejected by the tool itself. */
    public static IReadOnlyList<string> WriteTarget(JsonElement args, ToolRunContext exec, string property)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { } path
           && path.Trim().Length > 0
            ? [Resolve(exec, path)]
            : [];
}
