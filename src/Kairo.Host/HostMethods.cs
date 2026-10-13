using System.Reflection;
using System.Runtime.InteropServices;
using Kairo.Core.Rpc;
using Kairo.Core.Sessions;
using Kairo.Protocol;

namespace Kairo.Host;

/// <summary>Registers every protocol method on a dispatcher. This list is the whole surface the UI can reach.</summary>
public static class HostMethods
{
    public static void Register(
        RpcDispatcher rpc,
        AgentHost host,
        SettingsService settings,
        SessionStore store,
        McpService mcp,
        TerminalService terminals,
        UpdateService updates,
        Func<Task<string?>> pickFolder,
        string hostName)
    {
        var ctx = ProtocolJsonContext.Default;

        rpc.Register(Methods.AppInfo, ctx.Empty, ctx.AppInfo, (_, _) => Task.FromResult(new AppInfo(
            Name: "Kairo",
            Version: typeof(HostMethods).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            Runtime: RuntimeInformation.FrameworkDescription,
            Os: RuntimeInformation.OSDescription,
            Host: hostName)));

        rpc.Register(Methods.SettingsGet, ctx.Empty, ctx.Settings, (_, _) => Task.FromResult(settings.Get()));
        rpc.Register(Methods.SettingsUpdate, ctx.SettingsUpdate, ctx.Settings, (p, _) => Task.FromResult(settings.Update(p)));
        rpc.Register(Methods.SecretSet, ctx.SecretSetParams, ctx.Settings, (p, _) => Task.FromResult(settings.SetSecret(p)));

        rpc.Register(Methods.WorkspacePick, ctx.Empty, ctx.WorkspacePickResult, async (_, _) =>
        {
            var path = await pickFolder().ConfigureAwait(false);
            if (path is null)
                return new WorkspacePickResult(null);
            store.TouchWorkspace(path);
            return new WorkspacePickResult(ToWorkspace(path));
        });
        rpc.Register(Methods.WorkspaceList, ctx.Empty, ctx.WorkspaceListResult, (_, _) =>
            Task.FromResult(new WorkspaceListResult(store.ListWorkspaces().Where(Directory.Exists).Select(ToWorkspace).ToList())));

        rpc.Register(Methods.SessionList, ctx.Empty, ctx.SessionListResult, (_, _) => Task.FromResult(new SessionListResult(host.List())));
        rpc.Register(Methods.SessionCreate, ctx.SessionCreateParams, ctx.SessionSummary, (p, _) => Task.FromResult(host.Create(p.WorkingDirectory)));
        rpc.Register(Methods.SessionOpen, ctx.SessionRef, ctx.SessionDetail, (p, _) => Task.FromResult(host.Open(p.SessionId)));
        rpc.Register(Methods.SessionSend, ctx.SessionSendParams, ctx.Ok, (p, _) =>
        {
            host.Send(p.SessionId, p.Text);
            return Task.FromResult(new Ok());
        });
        rpc.Register(Methods.SessionCompact, ctx.SessionCompactParams, ctx.Ok, (p, _) =>
        {
            host.Compact(p.SessionId, p.Focus);
            return Task.FromResult(new Ok());
        });
        rpc.Register(Methods.SessionCancel, ctx.SessionRef, ctx.Ok, (p, _) =>
        {
            host.Cancel(p.SessionId);
            return Task.FromResult(new Ok());
        });
        rpc.Register(Methods.SessionSetMode, ctx.SessionModeParams, ctx.Ok, (p, _) =>
        {
            host.SetMode(p.SessionId, p.Mode);
            return Task.FromResult(new Ok());
        });

        rpc.Register(Methods.CheckpointPreview, ctx.CheckpointRef, ctx.CheckpointPreviewResult, (p, _) => Task.FromResult(host.PreviewCheckpoint(p.CheckpointId)));
        rpc.Register(Methods.CheckpointRollback, ctx.CheckpointRollbackParams, ctx.CheckpointRollbackResult,
            (p, _) => Task.FromResult(host.RollbackCheckpoint(p.CheckpointId, p.Paths, p.OverwriteConflicts)));

        rpc.Register(Methods.UpdateGet, ctx.Empty, ctx.UpdateStatus, (_, _) => Task.FromResult(updates.Status));
        rpc.Register(Methods.UpdateCheck, ctx.Empty, ctx.UpdateStatus, async (_, _) => await updates.CheckAsync().ConfigureAwait(false));
        rpc.Register(Methods.UpdateInstall, ctx.Empty, ctx.Ok, (_, _) =>
        {
            updates.Install();
            return Task.FromResult(new Ok());
        });

        rpc.Register(Methods.TerminalOpen, ctx.TerminalOpenParams, ctx.TerminalInfo, (p, _) => Task.FromResult(terminals.Open(p.WorkingDirectory, p.Columns, p.Rows)));
        rpc.Register(Methods.TerminalInput, ctx.TerminalInputParams, ctx.Ok, (p, _) =>
        {
            terminals.Input(p.TerminalId, p.Data);
            return Task.FromResult(new Ok());
        });
        rpc.Register(Methods.TerminalResize, ctx.TerminalResizeParams, ctx.Ok, (p, _) =>
        {
            terminals.Resize(p.TerminalId, p.Columns, p.Rows);
            return Task.FromResult(new Ok());
        });
        rpc.Register(Methods.TerminalClose, ctx.TerminalRef, ctx.Ok, (p, _) =>
        {
            terminals.Close(p.TerminalId);
            return Task.FromResult(new Ok());
        });

        rpc.Register(Methods.McpList, ctx.Empty, ctx.McpListResult, (_, _) => Task.FromResult(mcp.List()));
        rpc.Register(Methods.McpAdd, ctx.McpAddParams, ctx.McpListResult, (p, _) => Task.FromResult(mcp.Add(p)));
        rpc.Register(Methods.McpRemove, ctx.McpNameParams, ctx.McpListResult, (p, _) => Task.FromResult(mcp.Remove(p.Name)));
        rpc.Register(Methods.McpSetEnabled, ctx.McpEnabledParams, ctx.McpListResult, (p, _) => Task.FromResult(mcp.SetEnabled(p.Name, p.Enabled)));
        rpc.Register(Methods.McpReload, ctx.Empty, ctx.McpListResult, (_, _) => Task.FromResult(mcp.Reload()));
        rpc.Register(Methods.McpImportDesktop, ctx.Empty, ctx.McpListResult, (_, _) => Task.FromResult(mcp.ImportDesktop()));
        mcp.Changed += () => _ = rpc.NotifyAsync(Notifications.McpChanged, mcp.List(), ctx.McpListResult);

        rpc.Register(Methods.PermissionRespond, ctx.PermissionRespondParams, ctx.Ok, (p, _) =>
        {
            host.RespondPermission(p.RequestId, p.Decision);
            return Task.FromResult(new Ok());
        });
    }

    private static Workspace ToWorkspace(string path) =>
        new(path, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path);
}
