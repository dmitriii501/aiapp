using Kairo.Core.Config;
using Kairo.Core.Models;
using Kairo.Core.Secrets;
using Kairo.Providers.Anthropic;
using Kairo.Providers.OpenAICompatible;

namespace Kairo.Host;

public enum ProviderProblem { UnknownProvider, MissingKey, MissingBaseUrl, MissingModel }

public sealed class ProviderSetupException(ProviderProblem problem, string providerId)
    : Exception($"{providerId}: {problem}")
{
    public ProviderProblem Problem { get; } = problem;
    public string ProviderId { get; } = providerId;
}

/// <summary>Builds the configured model provider. Shared by the CLI and the desktop host.</summary>
public static class ProviderFactory
{
    public const string Anthropic = "anthropic";
    public const string OpenAICompatible = "openai-compatible";

    public static readonly IReadOnlyList<string> ProviderIds = [Anthropic, OpenAICompatible];

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string ProviderOf(KairoConfig config) => config.Provider ?? Anthropic;

    /// <summary>The endpoint the configured provider talks to, if it is not the vendor default.</summary>
    public static string? BaseUrlOf(KairoConfig config) =>
        ProviderOf(config) == Anthropic ? config.AnthropicBaseUrl : config.BaseUrl;

    public static string ModelOf(KairoConfig config) =>
        config.Model is { Length: > 0 } m ? m : ProviderOf(config) == Anthropic ? AnthropicProvider.DefaultModel : "";

    /// <summary>The environment variable wins over the stored key, so CI and scripts can override it.</summary>
    public static string? ApiKey(string providerId, ISecretStore secrets)
    {
        var env = Environment.GetEnvironmentVariable(providerId == Anthropic ? "ANTHROPIC_API_KEY" : "KAIRO_API_KEY");
        return !string.IsNullOrEmpty(env) ? env : secrets.Get(SecretNames.ApiKey(providerId));
    }

    /// <summary>Whether <see cref="Create"/> would succeed.</summary>
    public static ProviderProblem? Check(KairoConfig config, ISecretStore secrets)
    {
        var id = ProviderOf(config);
        return id switch
        {
            Anthropic when string.IsNullOrEmpty(ApiKey(id, secrets)) => ProviderProblem.MissingKey,
            Anthropic => null,
            OpenAICompatible when string.IsNullOrEmpty(config.BaseUrl) => ProviderProblem.MissingBaseUrl,
            OpenAICompatible when ModelOf(config).Length == 0 => ProviderProblem.MissingModel,
            OpenAICompatible => null,
            _ => ProviderProblem.UnknownProvider,
        };
    }

    public static IModelProvider Create(KairoConfig config, ISecretStore secrets)
    {
        var id = ProviderOf(config);
        if (Check(config, secrets) is { } problem)
            throw new ProviderSetupException(problem, id);

        return id switch
        {
            Anthropic => new AnthropicProvider(ApiKey(id, secrets)!, baseUrl: config.AnthropicBaseUrl),
            _ => new OpenAICompatibleProvider(Http, new Uri(config.BaseUrl!.TrimEnd('/') + "/"), ApiKey(id, secrets)),
        };
    }
}
