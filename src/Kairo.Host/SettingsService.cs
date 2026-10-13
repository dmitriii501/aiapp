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
            Ready: ProviderFactory.Check(config, Secrets) is null);
    }

    public Settings Update(SettingsUpdate update)
    {
        if (update.Provider is { } p && !ProviderFactory.ProviderIds.Contains(p))
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Неизвестный провайдер «{p}».");
        if (update.Effort is { } e && e != "default" && !Efforts.Contains(e))
            throw new RpcException(RpcErrorCodes.InvalidParams, $"Неизвестный уровень рассуждений «{e}».");
        if (update.BaseUrl is { Length: > 0 } url && !Uri.TryCreate(url, UriKind.Absolute, out _))
            throw new RpcException(RpcErrorCodes.InvalidParams, "Адрес API должен быть полным URL, например https://api.example.com/v1.");
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
        });
        return Get();
    }

    public Settings SetSecret(SecretSetParams p)
    {
        if (!ProviderFactory.ProviderIds.Contains(p.Provider))
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
