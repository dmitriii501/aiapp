using Kairo.Core.Rpc;
using Kairo.Mcp;
using Kairo.Protocol;

namespace Kairo.Host;

/// <summary>The UI's view of MCP servers: edits <c>mcp.json</c> on the user's request and reports connection state.</summary>
public sealed class McpService(McpManager manager, McpConfigStore store, string? claudeDesktopConfig = null)
{
    private readonly string _claudeDesktopConfig = claudeDesktopConfig ?? McpConfigStore.ClaudeDesktopPath;

    /// <summary>A server connected, failed or was edited. May fire on any thread.</summary>
    public event Action? Changed
    {
        add => manager.Changed += value;
        remove => manager.Changed -= value;
    }

    public McpListResult List(IReadOnlyList<string>? imported = null) =>
        new(manager.Servers.Select(s => new McpServerInfo(s.Name, s.Transport, s.Status.ToString().ToLowerInvariant(), s.Error, s.Tools)).ToList(),
            manager.ConfigPath, imported);

    public McpListResult Add(McpAddParams p)
    {
        var line = p.Command?.Trim();
        var url = p.Url?.Trim();
        McpServerConfig server;
        if (!string.IsNullOrEmpty(line))
        {
            var (command, args) = CommandLine.Split(line);
            server = new McpServerConfig { Command = command, Args = args.Length == 0 ? null : args };
        }
        else
        {
            server = new McpServerConfig { Url = url };
        }

        Edit(() => store.Add(p.Name, server));
        return Applied();
    }

    public McpListResult Remove(string name)
    {
        if (!Edit(() => store.Remove(name)))
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Сервер «{name}» не найден.");
        return Applied();
    }

    public McpListResult SetEnabled(string name, bool enabled)
    {
        Edit(() => store.SetDisabled(name, !enabled));
        return Applied();
    }

    public McpListResult ImportDesktop()
    {
        var imported = Edit(() => store.ImportFrom(_claudeDesktopConfig));
        if (imported.Count > 0)
            _ = manager.ReloadAsync();
        return List(imported);
    }

    public McpListResult Reload()
    {
        _ = manager.ReloadAsync();
        return List();
    }

    private McpListResult Applied()
    {
        _ = manager.ReloadAsync();
        return List();
    }

    /// <summary>Bad input from the user becomes a readable RPC error instead of an internal one.</summary>
    private static T Edit<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (McpConfigException ex)
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, ex.Message);
        }
    }

    private static void Edit(Action action) => Edit(() =>
    {
        action();
        return 0;
    });
}
