using Kairo.Core.Config;
using Kairo.Core.Permissions;
using Kairo.Core.Rpc;
using Kairo.Core.Secrets;
using Kairo.Protocol;

namespace Kairo.Host;

/// <summary>User-level settings (config.toml) and API keys (secret store) as the UI sees them.</summary>
public sealed class SettingsService(string userConfigDirectory, ISecretStore secrets)
{
    private static readonly HashSet<string> Efforts = ["low", "medium", "high", "xhigh", "max"];

    public string UserConfigDirectory { get; } = userConfigDirectory;

    public ISecretStore Secrets { get; } = secrets;

    /// <summary>What WebSearch uses right now. A broken config.toml means no search, not a crash in the middle of a turn.</summary>
    public Kairo.Tools.Web.WebSearchSettings WebSearch()
    {
        try
        {
            return ProviderFactory.WebSearchOf(LoadUser(), Secrets);
        }
        catch (FormatException)
        {
            return new Kairo.Tools.Web.WebSearchSettings();
        }
    }

    public KairoConfig LoadUser() => KairoConfig.Load(UserConfigDirectory, UserConfigDirectory, includeProject: false);

    /// <summary>The settings a session in <paramref name="workingDirectory"/> runs with (user + project config).</summary>
    public KairoConfig LoadFor(string workingDirectory) => KairoConfig.Load(workingDirectory, UserConfigDirectory);

    public Settings Get()
    {
        var config = LoadUser();
        return new Settings(
            Provider: ProviderFactory.ProviderOf(config),
            Model: ProviderFactory.ModelOf(config),
            BaseUrl: ProviderFactory.BaseUrlOf(config),
            Effort: config.Effort,
            Mode: ModeName(config.Mode ?? PermissionMode.Default),
            Providers: ProviderFactory.ProviderIds
                .Select(id => new ProviderStatus(id, !string.IsNullOrEmpty(ProviderFactory.ApiKey(id, Secrets))))
                .ToList(),
            Ready: ProviderFactory.Check(config, Secrets) is null,
            BraveKey: !string.IsNullOrEmpty(ProviderFactory.BraveKey(Secrets)),
            JevKey: !string.IsNullOrEmpty(ProviderFactory.JevKey(Secrets)),
            SandboxShell: config.SandboxShell ?? "off",
            SandboxNetwork: config.SandboxNetwork ?? false,
            SandboxRead: config.SandboxRead.ToList(),
            PcEnabled: config.PcEnabled ?? false,
            PcApps: config.PcApps.ToList());
    }

    public Settings Update(SettingsUpdate update)
    {
        if (update.Provider is { } p && !ProviderFactory.ProviderIds.Contains(p))
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Неизвестный провайдер «{p}».");
        if (update.Effort is { } e && e != "default" && !Efforts.Contains(e))
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Неизвестный уровень рассуждений «{e}».");
        if (update.BaseUrl is { Length: > 0 } url && !Uri.TryCreate(url, UriKind.Absolute, out _))
            throw new RpcException(RpcErrorCodes.InvalidParams, "Адрес API должен быть полным URL, например https://api.example.com/v1.");
        if (update.SandboxShell is { } sandboxShell && !KairoConfig.SandboxShellModes.Contains(sandboxShell))
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Неизвестный режим песочницы «{sandboxShell}».");
        var mode = update.Mode is { } m ? ParseMode(m) : (PermissionMode?)null;

        // The base URL belongs to the provider being saved (each has its own section).
        var targetProvider = update.Provider ?? ProviderFactory.ProviderOf(LoadUser());
        var urlSection = targetProvider == ProviderFactory.Anthropic ? "anthropic" : "openai_compatible";

        KairoConfig.UpdateUserFile(UserConfigDirectory, table =>
        {
            if (update.Provider is { } provider)
                KairoConfig.SetOrRemove(table, "provider", provider);
            if (update.Model is { } model)
                KairoConfig.SetOrRemove(table, "model", model.Trim() is { Length: > 0 } t ? t : null);
            if (update.Effort is { } effort)
                KairoConfig.SetOrRemove(table, "effort", effort == "default" ? null : effort);
            if (update.BaseUrl is { } baseUrl)
                KairoConfig.SetOrRemove(KairoConfig.Section(table, urlSection), "base_url", baseUrl.Trim() is { Length: > 0 } b ? b : null);
            if (mode is { } md)
                KairoConfig.SetOrRemove(KairoConfig.Section(table, "permissions"), "mode", ModeName(md));
            if (update.SandboxShell is { } shell)
                KairoConfig.SetOrRemove(KairoConfig.Section(table, "sandbox"), "shell", shell == "off" ? null : shell);
            if (update.PcEnabled is { } pcEnabled)
            {
                var pc = KairoConfig.Section(table, "pc");
                if (pcEnabled)
                    pc["enabled"] = true;
                else
                    pc.Remove("enabled");
            }
            if (update.SandboxNetwork is { } network)
            {
                var section = KairoConfig.Section(table, "sandbox");
                if (network)
                    section["network"] = true;
                else
                    section.Remove("network");
            }
        });
        return Get();
    }

    /// <summary>
    /// Allows Kairo to control a program. <paramref name="open"/> is what the host sees open right now: a name the UI made up is refused,
    /// so the list can only grow with programs the user is looking at.
    /// </summary>
    public Settings AddPcApp(string process, IReadOnlyCollection<string> open)
    {
        var name = Path.GetFileName(process.Trim()).ToLowerInvariant();
        if (!open.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Программа «{process}» сейчас не открыта или управление ею невозможно. Откройте её и повторите.");
        KairoConfig.UpdateUserFile(UserConfigDirectory, table => EditPcApps(table, list =>
        {
            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
                list.Add(name);
        }));
        return Get();
    }

    public Settings RemovePcApp(string process)
    {
        KairoConfig.UpdateUserFile(UserConfigDirectory, table => EditPcApps(table, list =>
            list.RemoveAll(p => string.Equals(p, process, StringComparison.OrdinalIgnoreCase))));
        return Get();
    }

    private static void EditPcApps(Tomlyn.Model.TomlTable table, Action<List<string>> edit)
    {
        var section = KairoConfig.Section(table, "pc");
        var list = section.TryGetValue("apps", out var v) && v is Tomlyn.Model.TomlArray array ? array.OfType<string>().ToList() : [];
        edit(list);
        if (list.Count == 0)
        {
            section.Remove("apps");
            return;
        }
        var result = new Tomlyn.Model.TomlArray();
        foreach (var item in list)
            result.Add(item);
        section["apps"] = result;
    }

    /// <summary>Adds a folder (chosen in the native picker) that sandboxed commands may read.</summary>
    public Settings AddSandboxRead(string path)
    {
        var full = Path.GetFullPath(path);
        KairoConfig.UpdateUserFile(UserConfigDirectory, table => EditSandboxRead(table, list =>
        {
            if (!list.Any(p => string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase)))
                list.Add(full.Replace('\\', '/'));
        }));
        return Get();
    }

    /// <summary>Removes an entry that is on the list; anything else is ignored, so the UI cannot name a path of its own.</summary>
    public Settings RemoveSandboxRead(string path)
    {
        KairoConfig.UpdateUserFile(UserConfigDirectory, table => EditSandboxRead(table, list =>
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))));
        return Get();
    }

    private static void EditSandboxRead(Tomlyn.Model.TomlTable table, Action<List<string>> edit)
    {
        var section = KairoConfig.Section(table, "sandbox");
        var list = section.TryGetValue("read", out var v) && v is Tomlyn.Model.TomlArray array ? array.OfType<string>().ToList() : [];
        edit(list);
        if (list.Count == 0)
        {
            section.Remove("read");
            return;
        }
        var result = new Tomlyn.Model.TomlArray();
        foreach (var item in list)
            result.Add(item);
        section["read"] = result;
    }

    public Settings SetSecret(SecretSetParams p)
    {
        if (p.Provider is not (SecretNames.BraveSearch or SecretNames.Jev) && !ProviderFactory.ProviderIds.Contains(p.Provider))
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Неизвестный провайдер «{p.Provider}».");
        var name = SecretNames.ApiKey(p.Provider);
        if (p.Key.Trim() is { Length: > 0 } key)
            Secrets.Set(name, key);
        else
            Secrets.Delete(name);
        return Get();
    }

    public static string ModeName(PermissionMode mode) => mode switch
    {
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Plan => "plan",
        PermissionMode.Bypass => "bypass",
        _ => "default",
    };

    public static PermissionMode ParseMode(string mode)
    {
        try
        {
            return KairoConfig.ParseMode(mode);
        }
        catch (FormatException)
        {
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Неизвестный режим «{mode}».");
        }
    }
}
