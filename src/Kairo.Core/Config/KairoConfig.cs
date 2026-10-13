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
/// mode = "default"           # default | acceptEdits | plan | bypass (nothing asks; only deny rules apply)
/// allow = ["Shell(git status)", "Shell(dotnet test:*)"]
/// ask = ["Shell(git push:*)"]
/// deny = ["Read(**/secrets/**)"]
///
/// [openai_compatible]
/// base_url = "http://localhost:11434/v1"
///
/// [anthropic]
/// base_url = "https://my-gateway.example.com"   # optional, for Anthropic-format gateways
///
/// [context]
/// window = 200000                                # tokens; 0 turns automatic compaction off
/// compact_at = 0.8                               # summarize long sessions at this share of the window
///
/// [web]
/// search = "searxng"                             # "brave" (key in settings) or "searxng"
/// searxng_url = "http://localhost:8080"          # your SearXNG instance, JSON format enabled
///
/// [update]
/// check = false                                 # no automatic update checks
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

    /// <summary>Check for updates on start and every few hours (<c>[update] check</c>, default true).</summary>
    public bool? UpdateCheck { get; set; }

    /// <summary>Overrides the update feed (<c>[update] url</c>): a GitHub repository, a web address or a folder. Read from the user's file only.</summary>
    public string? UpdateUrl { get; set; }

    /// <summary>Web search backend (<c>[web] search</c>): "brave" or "searxng". Read from the user's file only.</summary>
    public string? WebSearch { get; set; }

    /// <summary>Address of a SearXNG instance (<c>[web] searxng_url</c>). Read from the user's file only: it receives the agent's queries.</summary>
    public string? SearxngUrl { get; set; }

    public const int DefaultContextWindow = 128_000;
    public const double DefaultCompactAt = 0.8;

    /// <summary>Size of the model's context in tokens (<c>[context] window</c>); 0 turns automatic compaction off.</summary>
    public int? ContextWindow { get; set; }

    /// <summary>Share of the window at which long sessions are summarized (<c>[context] compact_at</c>, 0.3 to 0.95).</summary>
    public double? CompactAt { get; set; }

    public int EffectiveContextWindow => ContextWindow ?? DefaultContextWindow;
    public double EffectiveCompactAt => CompactAt ?? DefaultCompactAt;

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
        {
            // Updates replace the app itself: a repository must not be able to steer them.
            // Likewise the search backend: it receives what the agent searches for.
            var userMode = config.Mode;
            var (check, url, search, searxng) = (config.UpdateCheck, config.UpdateUrl, config.WebSearch, config.SearxngUrl);
            config.Merge(Path.Combine(workingDirectory, ".kairo", "config.toml"));
            (config.UpdateCheck, config.UpdateUrl, config.WebSearch, config.SearxngUrl) = (check, url, search, searxng);
            // A repository cannot switch off the user's confirmations.
            if (config.Mode == PermissionMode.Bypass && userMode != PermissionMode.Bypass)
                config.Mode = userMode;
        }
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
        "bypass" or "bypasspermissions" => PermissionMode.Bypass,
        _ => throw new FormatException($"Unknown permission mode '{value}'. Use default, acceptEdits, plan or bypass."),
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

        if (table.TryGetValue("context", out var c) && c is TomlTable context)
        {
            if (context.TryGetValue("window", out var window) && window is long tokens)
                ContextWindow = (int)Math.Clamp(tokens, 0, int.MaxValue);
            if (context.TryGetValue("compact_at", out var at))
            {
                var share = at switch { double d => d, long l => l, _ => (double?)null };
                if (share is { } s)
                    CompactAt = Math.Clamp(s, 0.3, 0.95);
            }
        }

        if (table.TryGetValue("web", out var w) && w is TomlTable web)
        {
            WebSearch = String(web, "search") ?? WebSearch;
            SearxngUrl = String(web, "searxng_url") ?? SearxngUrl;
        }

        if (table.TryGetValue("update", out var u) && u is TomlTable update)
        {
            if (update.TryGetValue("check", out var check) && check is bool enabled)
                UpdateCheck = enabled;
            UpdateUrl = String(update, "url") ?? UpdateUrl;
        }

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
