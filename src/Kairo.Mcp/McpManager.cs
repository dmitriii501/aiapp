using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kairo.Core.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kairo.Mcp;

public enum McpStatus { Connecting, Ready, Failed, Disabled }

public sealed record McpServerState(string Name, string Transport, McpStatus Status, string? Error, IReadOnlyList<string> Tools);

/// <summary>
/// Connects to the servers in <c>mcp.json</c> and exposes their tools as <c>mcp__server__tool</c>.
/// One failing server never blocks the others or the agent: it is reported as <see cref="McpStatus.Failed"/>.
/// A reload keeps connections whose configuration did not change.
/// </summary>
public sealed partial class McpManager(McpConfigStore store, Func<string, McpServerConfig, Action<string>, IClientTransport>? transportFactory = null) : IAsyncDisposable
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private const int MaxToolNameLength = 64;

    private sealed record Connection(string ConfigKey, McpClient Client, IList<McpClientTool> Tools);

    private readonly Func<string, McpServerConfig, Action<string>, IClientTransport> _transportFactory = transportFactory ?? CreateTransport;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _reload = new(1, 1);
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private List<McpServerState> _states = [];
    private List<ITool> _tools = [];

    /// <summary>Raised when a server's status or tool list changes. May fire on any thread.</summary>
    public event Action? Changed;

    public string ConfigPath => store.Path;

    public IReadOnlyList<McpServerState> Servers
    {
        get { lock (_gate) return _states; }
    }

    public IReadOnlyList<ITool> Tools
    {
        get { lock (_gate) return _tools; }
    }

    /// <summary>Applies the current <c>mcp.json</c>. Returns when every enabled server is ready or has failed.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _reload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyDictionary<string, McpServerConfig> config;
            try
            {
                config = store.Load();
            }
            catch (McpConfigException ex)
            {
                await DisconnectAsync(_ => true).ConfigureAwait(false);
                SetStates([new McpServerState(Path.GetFileName(store.Path), "file", McpStatus.Failed, ex.Message, [])], publishTools: true);
                return;
            }

            var names = config.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var keys = names.ToDictionary(n => n, n => JsonSerializer.Serialize(config[n], McpJsonContext.Default.McpServerConfig));

            // Drop what was removed, disabled or changed.
            await DisconnectAsync(name => !config.TryGetValue(name, out var c) || c.Disabled == true || _connections[name].ConfigKey != keys[name]).ConfigureAwait(false);

            var states = new ConcurrentDictionary<string, McpServerState>();
            foreach (var name in names)
            {
                var server = config[name];
                var status = server.Disabled == true ? McpStatus.Disabled : _connections.ContainsKey(name) ? McpStatus.Ready : McpStatus.Connecting;
                states[name] = new McpServerState(name, server.Transport, status, null, _connections.TryGetValue(name, out var live) ? live.Tools.Select(t => t.Name).ToList() : []);
            }
            Publish(states, names);

            var added = new ConcurrentDictionary<string, Connection>();
            await Task.WhenAll(names.Where(n => config[n].Disabled != true && !_connections.ContainsKey(n)).Select(async name =>
            {
                try
                {
                    var connection = await ConnectAsync(name, config[name], keys[name], ct).ConfigureAwait(false);
                    added[name] = connection;
                    states[name] = new McpServerState(name, config[name].Transport, McpStatus.Ready, null, connection.Tools.Select(t => t.Name).ToList());
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    states[name] = new McpServerState(name, config[name].Transport, McpStatus.Failed, ex.Message, []);
                }
                Publish(states, names);
            })).ConfigureAwait(false);

            lock (_gate)
            {
                foreach (var (name, connection) in added)
                    _connections[name] = connection;
            }
            SetStates(names.Select(n => states[n]).ToList(), publishTools: true);
        }
        finally
        {
            _reload.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reload.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectAsync(_ => true).ConfigureAwait(false);
        }
        finally
        {
            _reload.Release();
        }
    }

    private async Task DisconnectAsync(Func<string, bool> predicate)
    {
        List<Connection> dropped;
        lock (_gate)
        {
            var names = _connections.Keys.Where(predicate).ToList();
            dropped = names.Select(n => _connections[n]).ToList();
            foreach (var name in names)
                _connections.Remove(name);
        }
        foreach (var connection in dropped)
        {
            try
            {
                await connection.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutting down a server that already died is not an error.
            }
        }
    }

    private async Task<Connection> ConnectAsync(string name, McpServerConfig server, string configKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        var stderr = new StringBuilder();
        void OnStderr(string line)
        {
            lock (stderr)
            {
                stderr.AppendLine(line);
                if (stderr.Length > 600)
                    stderr.Remove(0, stderr.Length - 600);
            }
        }
        string WithStderr(string message)
        {
            lock (stderr)
                return stderr.Length == 0 || message.Contains("stderr", StringComparison.OrdinalIgnoreCase)
                    ? message
                    : $"{message} Вывод сервера: {stderr.ToString().Trim().ReplaceLineEndings(" ")}";
        }

        McpClient? client = null;
        try
        {
            var options = new McpClientOptions { ClientInfo = new Implementation { Name = "Kairo", Version = "0.1.0" } };
            client = await McpClient.CreateAsync(_transportFactory(name, server, OnStderr), options, cancellationToken: timeout.Token).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            return new Connection(configKey, client, tools);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException(WithStderr($"Сервер не ответил за {ConnectTimeout.TotalSeconds:0} с."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(WithStderr(Describe(ex)), ex);
        }
    }

    private void Publish(ConcurrentDictionary<string, McpServerState> states, List<string> order) =>
        SetStates(order.Select(n => states[n]).ToList(), publishTools: false);

    private void SetStates(List<McpServerState> states, bool publishTools)
    {
        lock (_gate)
        {
            _states = states;
            if (publishTools)
                _tools = BuildTools();
        }
        Changed?.Invoke();
    }

    private List<ITool> BuildTools()
    {
        var exposed = new List<ITool>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in _connections.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var connection = _connections[name];
            foreach (var tool in connection.Tools)
                exposed.Add(new McpTool(UniqueName(name, tool.Name, used), name, connection.Client, tool.ProtocolTool));
        }
        return exposed;
    }

    private static IClientTransport CreateTransport(string name, McpServerConfig server, Action<string> onStderr)
    {
        if (server.IsRemote)
        {
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = name,
                Endpoint = new Uri(server.Url!),
                AdditionalHeaders = server.Headers,
            });
        }

        if (!CommandResolver.Exists(server.Command!, server.Cwd))
            throw new FileNotFoundException($"Команда «{server.Command}» не найдена. Проверьте, что программа установлена и есть в PATH.");

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = server.Command!,
            Arguments = server.Args ?? [],
            EnvironmentVariables = server.Env?.ToDictionary(p => p.Key, p => (string?)p.Value),
            WorkingDirectory = server.Cwd,
            StandardErrorLines = onStderr,
        });
    }

    private static string Describe(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ");
        if (ex.InnerException is { } inner && !message.Contains(inner.Message, StringComparison.Ordinal))
            message += " " + inner.Message.ReplaceLineEndings(" ");
        return message.Length <= 400 ? message : message[..400] + "…";
    }

    /// <summary>Tool names must fit <c>[A-Za-z0-9_-]{1,64}</c> for the model APIs, and be unique.</summary>
    internal static string UniqueName(string server, string tool, HashSet<string> used)
    {
        var name = $"mcp__{Sanitize(server)}__{Sanitize(tool)}";
        if (name.Length > MaxToolNameLength)
            name = name[..MaxToolNameLength];

        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
        {
            var suffix = $"_{n}";
            candidate = name[..Math.Min(name.Length, MaxToolNameLength - suffix.Length)] + suffix;
        }
        return candidate;
    }

    private static string Sanitize(string text) => NotAllowed().Replace(text.Trim(), "_");

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex NotAllowed();
}
