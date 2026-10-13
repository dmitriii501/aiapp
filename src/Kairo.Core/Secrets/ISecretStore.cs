namespace Kairo.Core.Secrets;

/// <summary>
/// Where API keys and tokens live. The Windows implementation is Credential Manager; secrets never go to
/// config files, logs or the UI (docs/SECURITY-MODEL.md).
/// </summary>
public interface ISecretStore
{
    string? Get(string name);

    void Set(string name, string secret);

    void Delete(string name);
}

public static class SecretNames
{
    /// <summary>The credential name for a provider's API key, e.g. <c>Kairo/anthropic</c>.</summary>
    public static string ApiKey(string providerId) => $"Kairo/{providerId}";

    /// <summary>The key of the Brave Search API, used by the WebSearch tool.</summary>
    public const string BraveSearch = "brave";
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string name) => _secrets.GetValueOrDefault(name);

    public void Set(string name, string secret) => _secrets[name] = secret;

    public void Delete(string name) => _secrets.Remove(name);
}
