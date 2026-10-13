using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairo.Protocol;

/// <summary>JSON-RPC 2.0 envelope as it arrives from a client. A message without an id is a notification.</summary>
public sealed record RpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("id")] JsonElement? Id = null,
    [property: JsonPropertyName("params")] JsonElement? Params = null);

public sealed record RpcResponse(
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result")] JsonElement? Result = null,
    [property: JsonPropertyName("error")] RpcError? Error = null)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";
}

public sealed record RpcNotification(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params = null)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";
}

public sealed record RpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Standard JSON-RPC 2.0 error codes.</summary>
public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}
