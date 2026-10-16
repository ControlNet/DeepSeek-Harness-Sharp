using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Gui.Services;

/** Sidebar history merges persisted pre-Fleet sessions with the local Host registry; only Host writes sessions. */
public sealed class SessionCatalog(Context ctx, FleetWorkspace fleet)
{
    /** 空 cwd 会话的占位分组名。 */
    public const string UnspecifiedWorkspace = "未指定目录";

    public IReadOnlyList<SessionNodeViewModel> Load()
    {
        var nodes = new Dictionary<string, SessionNodeViewModel>(StringComparer.Ordinal);
        var persistence = ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, strict: false);
        foreach (var snapshot in persistence?.List() ?? [])
        {
            if (snapshot.Header.IsSubagent)
                continue;
            nodes[snapshot.Header.Id.Value] = Create(snapshot.Header);
        }

        foreach (var session in fleet.Find(MachineSpec.LocalName)?.Sessions ?? [])
        {
            if (session.Kind != HostSessionKinds.Harness)
                continue;
            var path = WorkspacePath(session.ProjectPath);
            nodes[session.SessionId] = new SessionNodeViewModel
            {
                SessionId = SessionId.Create(session.SessionId),
                Workspace = WorkspaceDisplayName(path),
                WorkspacePath = path,
                Cwd = session.ProjectPath ?? "",
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(session.CreatedAt),
                Title = session.Title ?? session.SessionId,
                IsLive = session.Status != HostSessionStatuses.Exited,
                IsRunning = session.Status == HostSessionStatuses.Running,
                RelativeTime = RelativeTimeText.Format(DateTimeOffset.FromUnixTimeMilliseconds(session.LastActivityAt)),
            };
        }

        return [.. nodes.Values.OrderByDescending(node => node.CreatedAt)];
    }


    private static SessionNodeViewModel Create(SessionHeader header)
    {
        var workspacePath = WorkspacePath(header.Cwd);
        var node = new SessionNodeViewModel
        {
            SessionId = header.Id,
            Workspace = WorkspaceDisplayName(workspacePath),
            WorkspacePath = workspacePath,
            Cwd = header.Cwd ?? "",
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(header.CreatedAt),
            Title = header.Title ?? header.Id.Value,
            RelativeTime = RelativeTimeText.Format(DateTimeOffset.FromUnixTimeMilliseconds(header.CreatedAt)),
        };
        return node;
    }

    /** 分组键: 归一化后的完整路径; 空 cwd 归到一个占位组。 */
    public static string WorkspacePath(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return UnspecifiedWorkspace;
        try
        {
            return Path.GetFullPath(cwd.Trim());
        }
        catch (Exception)
        {
            return cwd.Trim();
        }
    }

    /** 显示名: 末段目录名; 占位分组原样返回。 */
    public static string WorkspaceDisplayName(string path)
    {
        if (path == UnspecifiedWorkspace)
            return path;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }
}
