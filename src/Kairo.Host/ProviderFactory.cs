using Kairo.Core.Config;
using Kairo.Core.Decisions;
using Kairo.Core.Models;
using Kairo.Core.Secrets;
using Kairo.Tools.Web;
using Kairo.Providers.Anthropic;
using Kairo.Providers.Jev;
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

    /// <summary>The Brave Search key for WebSearch: the environment variable wins over the stored key.</summary>
    public static string? BraveKey(ISecretStore secrets)
    {
        var env = Environment.GetEnvironmentVariable("BRAVE_API_KEY");
        return !string.IsNullOrEmpty(env) ? env : secrets.Get(SecretNames.ApiKey(SecretNames.BraveSearch));
    }

    /// <summary>The Jev (TypeSafe) key: the environment variable the vendor documents wins over the stored key.</summary>
    public static string? JevKey(ISecretStore secrets)
    {
        var env = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        return !string.IsNullOrEmpty(env) ? env : secrets.Get(SecretNames.ApiKey(SecretNames.Jev));
    }

    /// <summary>The fast decision model, or null when there is no Jev key (PC control then runs on the big model alone).</summary>
    public static IDecisionModel? DecisionModelOf(KairoConfig config, ISecretStore secrets)
    {
        if (JevKey(secrets) is not { Length: > 0 } key)
            return null;
        return new JevDecisionModel(
            key,
            baseUri: config.DecisionUrl is { } url ? new Uri(url) : null,
            model: config.DecisionModel,
            deadline: config.DecisionDeadlineMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            visionModel: config.DecisionVisionModel);
    }

    public static WebSearchSettings WebSearchOf(KairoConfig config, ISecretStore secrets) =>
        new(BraveKey(secrets), config.SearxngUrl, config.WebSearch);

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
            Anthropic => new AnthropicProvider(ApiKey(id, secrets)!, baseUrl: config.AnthropicBaseUrl, promptCache: config.AnthropicPromptCache ?? true),
            _ => new OpenAICompatibleProvider(Http, new Uri(config.BaseUrl!.TrimEnd('/') + "/"), ApiKey(id, secrets)),
        };
    }
}
