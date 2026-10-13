using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kairo.Mcp;

/// <summary>
/// One server entry, in the same shape as Claude Desktop's <c>mcpServers</c>: a local process
/// (<c>command</c>, <c>args</c>, <c>env</c>) or a remote endpoint (<c>url</c>, <c>headers</c>).
/// </summary>
public sealed record McpServerConfig
{
    [JsonPropertyName("command")] public string? Command { get; init; }
    [JsonPropertyName("args")] public string[]? Args { get; init; }
    [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; init; }
    [JsonPropertyName("cwd")] public string? Cwd { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; init; }
    [JsonPropertyName("disabled")] public bool? Disabled { get; init; }

    public bool IsRemote => !string.IsNullOrWhiteSpace(Url);

    public string Transport => IsRemote ? "http" : "stdio";
}

public sealed class McpConfigFile
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpConfigFile))]
[JsonSerializable(typeof(McpServerConfig))]
internal sealed partial class McpJsonContext : JsonSerializerContext;

public sealed class McpConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The user's <c>mcp.json</c>. Servers here can start processes with the user's rights, so this file is only
/// ever written on the user's own request. Project-level configs are not read.
/// </summary>
public sealed partial class McpConfigStore(string path)
{
    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kairo", "mcp.json");

    public static string ClaudeDesktopPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");

    /// <summary>
    /// Where Claude Desktop may keep its config: the usual roaming folder and, for the Microsoft Store (MSIX) build,
    /// the package's own copy of it. Only files that exist are returned.
    /// </summary>
    public static IReadOnlyList<string> ClaudeDesktopCandidates()
    {
        var candidates = new List<string> { ClaudeDesktopPath };
        var packages = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.EnumerateDirectories(packages, "Claude_*"))
                candidates.Add(System.IO.Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
        }
        return candidates.Where(File.Exists).ToList();
    }

    private readonly Lock _gate = new();

    public IReadOnlyDictionary<string, McpServerConfig> Load()
    {
        lock (_gate)
            return Read(Path).Servers;
    }

    public void Add(string name, McpServerConfig server)
    {
        name = name.Trim();
        if (!ValidName().IsMatch(name))
            throw new McpConfigException("Имя сервера: до 40 символов, буквы, цифры, пробел, «-», «_» и «.».");
        if (string.IsNullOrWhiteSpace(server.Command) == string.IsNullOrWhiteSpace(server.Url))
            throw new McpConfigException("Укажите либо команду запуска, либо адрес (URL) сервера.");
        if (server.IsRemote && !(Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            throw new McpConfigException("Адрес сервера должен начинаться с http:// или https://.");

        lock (_gate)
        {
            var file = Read(Path);
            if (file.Servers.ContainsKey(name))
                throw new McpConfigException($"Сервер «{name}» уже есть. Удалите его или выберите другое имя.");
            file.Servers[name] = server;
            Write(file);
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var file = Read(Path);
            if (!file.Servers.Remove(name))
                return false;
            Write(file);
            return true;
        }
    }

    public void SetDisabled(string name, bool disabled)
    {
        lock (_gate)
        {
            var file = Read(Path);
            if (!file.Servers.TryGetValue(name, out var server))
                throw new McpConfigException($"Сервер «{name}» не найден.");
            file.Servers[name] = server with { Disabled = disabled ? true : null };
            Write(file);
        }
    }

    /// <summary>Copies servers from another config in the same format (Claude Desktop). Existing names are kept.</summary>
    public IReadOnlyList<string> ImportFrom(string otherPath)
    {
        if (!File.Exists(otherPath))
            throw new McpConfigException("Файл конфигурации Claude Desktop не найден.");

        var other = Read(otherPath);
        lock (_gate)
        {
            var file = Read(Path);
            var imported = new List<string>();
            foreach (var (name, server) in other.Servers)
            {
                if (file.Servers.TryAdd(name, server))
                    imported.Add(name);
            }
            if (imported.Count > 0)
                Write(file);
            return imported;
        }
    }

    private static McpConfigFile Read(string path)
    {
        if (!File.Exists(path))
            return new McpConfigFile();
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
                return new McpConfigFile();
            return JsonSerializer.Deserialize(text, McpJsonContext.Default.McpConfigFile) ?? new McpConfigFile();
        }
        catch (JsonException ex)
        {
            throw new McpConfigException($"Не удалось прочитать {path}: {ex.Message}", ex);
        }
    }

    private void Write(McpConfigFile file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, McpJsonContext.Default.McpConfigFile), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }

    [GeneratedRegex(@"^[\w][\w .\-]{0,39}$")]
    private static partial Regex ValidName();
}

/// <summary>Splits a command line like <c>npx -y server "C:\my folder"</c> into program and arguments.</summary>
public static class CommandLine
{
    public static (string Command, string[] Args) Split(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                    parts.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started)
            parts.Add(current.ToString());

        return parts.Count == 0 ? ("", []) : (parts[0], parts.Skip(1).ToArray());
    }
}
