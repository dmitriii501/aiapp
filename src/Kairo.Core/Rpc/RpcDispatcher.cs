using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kairo.Protocol;

namespace Kairo.Core.Rpc;

/// <summary>
/// Transport-agnostic JSON-RPC 2.0 dispatcher. The host feeds it raw messages and supplies
/// <c>send</c> for outgoing ones; WebView2, named pipes and WebSockets all plug in the same way.
/// Only explicitly registered methods exist — anything else is answered with MethodNotFound.
/// </summary>
public sealed class RpcDispatcher
{
    private delegate Task<JsonElement?> Handler(JsonElement? @params, CancellationToken ct);

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private readonly ConcurrentDictionary<string, Handler> _handlers = new(StringComparer.Ordinal);
    private readonly Func<string, Task> _send;

    public RpcDispatcher(Func<string, Task> send) => _send = send;

    public void Register<TParams, TResult>(
        string method,
        JsonTypeInfo<TParams> paramsType,
        JsonTypeInfo<TResult> resultType,
        Func<TParams, CancellationToken, Task<TResult>> handler)
    {
        var added = _handlers.TryAdd(method, async (raw, ct) =>
        {
            var el = raw is { ValueKind: not JsonValueKind.Null } r ? r : EmptyObject;
            var p = el.Deserialize(paramsType);
            if (p is null)
                throw new RpcException(RpcErrorCodes.InvalidParams, $"Invalid params for '{method}'.");

            var result = await handler(p, ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, resultType);
        });

        if (!added)
            throw new InvalidOperationException($"Method '{method}' is already registered.");
    }

    public Task NotifyAsync<T>(string method, T payload, JsonTypeInfo<T> payloadType)
    {
        var notification = new RpcNotification(method, JsonSerializer.SerializeToElement(payload, payloadType));
        return _send(JsonSerializer.Serialize(notification, ProtocolJsonContext.Default.RpcNotification));
    }

    /// <summary>Handles one incoming message. Never throws: protocol errors become JSON-RPC error responses.</summary>
    public async Task HandleAsync(string json, CancellationToken ct = default)
    {
        RpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.RpcRequest);
        }
        catch (JsonException)
        {
            await ReplyErrorAsync(null, RpcErrorCodes.ParseError, "Parse error.").ConfigureAwait(false);
            return;
        }

        if (request is null || request.JsonRpc != "2.0" || string.IsNullOrEmpty(request.Method))
        {
            await ReplyErrorAsync(request?.Id, RpcErrorCodes.InvalidRequest, "Invalid request.").ConfigureAwait(false);
            return;
        }

        var isNotification = request.Id is null;

        if (!_handlers.TryGetValue(request.Method, out var handler))
        {
            if (!isNotification)
                await ReplyErrorAsync(request.Id, RpcErrorCodes.MethodNotFound, $"Method '{request.Method}' not found.").ConfigureAwait(false);
            return;
        }

        RpcResponse response;
        try
        {
            var result = await handler(request.Params, ct).ConfigureAwait(false);
            response = new RpcResponse(request.Id, Result: result);
        }
        catch (RpcException ex)
        {
            response = new RpcResponse(request.Id, Error: new RpcError(ex.Code, ex.Message));
        }
        catch (JsonException)
        {
            response = new RpcResponse(request.Id, Error: new RpcError(RpcErrorCodes.InvalidParams, $"Invalid params for '{request.Method}'."));
        }
        catch (Exception)
        {
            // Internal details never cross the bridge.
            response = new RpcResponse(request.Id, Error: new RpcError(RpcErrorCodes.InternalError, "Internal error."));
        }

        if (!isNotification)
            await _send(JsonSerializer.Serialize(response, ProtocolJsonContext.Default.RpcResponse)).ConfigureAwait(false);
    }

    private Task ReplyErrorAsync(JsonElement? id, int code, string message) =>
        _send(JsonSerializer.Serialize(new RpcResponse(id, Error: new RpcError(code, message)), ProtocolJsonContext.Default.RpcResponse));
}

public sealed class RpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
