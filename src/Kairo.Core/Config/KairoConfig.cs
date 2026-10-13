using Kairo.Core.Permissions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Kairo.Core.Config;

/// <summary>
/// Settings from <c>%APPDATA%\Kairo\config.toml</c> merged with <c>&lt;project&gt;\.kairo\config.toml</c>.
/// Scalars from the project win; permission rule lists are combined.
/// </summary>
/// <example>
/// provider = "anthropic"
/// model = "claude-opus-5-5"
/// effort = "high"
///
/// [permissions]
/// mode = "default"           # default | acceptEdits | plan
/// allow = ["Shell(git status)", "Shell(dotnet test:*)"]
/// ask = ["Shell(git push:*)"]
/// deny = ["Read(**/secrets/**)"]
///
/// [openai_compatible]
/// base_url = "http://localhost:11434/v1"
///
/// [anthropic]
/// base_url = "https://my-gateway.example.com"   # optional, for Anthropic-format gateways
/// </example>
public sealed class KairoConfig
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    /// <summary>Endpoint for the openai-compatible provider (<c>[openai_compatible] base_url</c>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Optional endpoint for Anthropic-format gateways and proxies (<c>[anthropic] base_url</c>).</summary>
    public string? AnthropicBaseUrl { get; set; }
    public PermissionMode? Mode { get; set; }
    public List<string> Allow { get; } = [];
    public List<string> Ask { get; } = [];
    public List<string> Deny { get; } = [];

    /// <summary>Files that were loaded, outermost first.</summary>
    public List<string> Sources { get; } = [];

    public static string UserConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kairo");

    public static KairoConfig Load(string workingDirectory, string? userConfigDirectory, bool includeProject = true)
    {
        var config = new KairoConfig();
        if (userConfigDirectory is not null)
            config.Merge(Path.Combine(userConfigDirectory, "config.toml"));
        if (includeProject)
            config.Merge(Path.Combine(workingDirectory, ".kairo", "config.toml"));
        return config;
    }

    public PermissionRules BuildRules()
    {
        var rules = PermissionRules.WithDefaults();
        rules.Allow.AddRange(Allow.Select(PermissionRule.Parse));
        rules.Ask.AddRange(Ask.Select(PermissionRule.Parse));
        rules.Deny.AddRange(Deny.Select(PermissionRule.Parse));
        return rules;
    }

    public static PermissionMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "default" => PermissionMode.Default,
        "acceptedits" or "accept-edits" => PermissionMode.AcceptEdits,
        "plan" => PermissionMode.Plan,
        _ => throw new FormatException($"Unknown permission mode '{value}'. Use default, acceptEdits or plan."),
    };

    /// <summary>Read-modify-write of the user's config.toml; keys the update does not touch are kept.</summary>
    public static void UpdateUserFile(string userConfigDirectory, Action<TomlTable> update)
    {
        var path = Path.Combine(userConfigDirectory, "config.toml");
        var table = File.Exists(path)
            ? TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path), ConfigTomlContext.Default) ?? new TomlTable()
            : new TomlTable();
        update(table);
        Directory.CreateDirectory(userConfigDirectory);
        File.WriteAllText(path, TomlSerializer.Serialize(table, ConfigTomlContext.Default));
    }

    /// <summary>Sets <paramref name="key"/> in <paramref name="table"/>, or removes it when <paramref name="value"/> is null.</summary>
    public static void SetOrRemove(TomlTable table, string key, string? value)
    {
        if (value is null)
            table.Remove(key);
        else
            table[key] = value;
    }

    public static TomlTable Section(TomlTable table, string name)
    {
        if (table.TryGetValue(name, out var v) && v is TomlTable section)
            return section;
        var created = new TomlTable();
        table[name] = created;
        return created;
    }

    internal void MergeToml(string text, string source)
    {
        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(text, ConfigTomlContext.Default)!;
        }
        catch (TomlException ex)
        {
            throw new FormatException($"{source}: {ex.Message}", ex);
        }

        Provider = String(table, "provider") ?? Provider;
        Model = String(table, "model") ?? Model;
        Effort = String(table, "effort") ?? Effort;

        if (table.TryGetValue("permissions", out var p) && p is TomlTable permissions)
        {
            if (String(permissions, "mode") is { } mode)
                Mode = ParseMode(mode);
            Allow.AddRange(Strings(permissions, "allow"));
            Ask.AddRange(Strings(permissions, "ask"));
            Deny.AddRange(Strings(permissions, "deny"));
        }

        if (table.TryGetValue("openai_compatible", out var o) && o is TomlTable openai)
            BaseUrl = String(openai, "base_url") ?? BaseUrl;

        if (table.TryGetValue("anthropic", out var a) && a is TomlTable anthropic)
            AnthropicBaseUrl = String(anthropic, "base_url") ?? AnthropicBaseUrl;

        Sources.Add(source);
    }

    private void Merge(string path)
    {
        if (File.Exists(path))
            MergeToml(File.ReadAllText(path), path);
    }

    private static string? String(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is string s ? s : null;

    private static IEnumerable<string> Strings(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is TomlArray array ? array.OfType<string>() : [];
}

/// <summary>Source-generated TOML metadata so config parsing stays AOT compatible.</summary>
[TomlSerializable(typeof(TomlTable))]
internal sealed partial class ConfigTomlContext : TomlSerializerContext;
