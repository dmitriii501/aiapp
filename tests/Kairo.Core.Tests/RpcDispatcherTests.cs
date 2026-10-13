using System.Collections.Concurrent;
using System.Text.Json;
using Kairo.Core.Rpc;
using Kairo.Protocol;

namespace Kairo.Core.Tests;

public class RpcDispatcherTests
{
    private readonly ConcurrentQueue<JsonElement> _sent = new();
    private readonly RpcDispatcher _rpc;

    public RpcDispatcherTests()
    {
        _rpc = new RpcDispatcher(json =>
        {
            _sent.Enqueue(JsonDocument.Parse(json).RootElement.Clone());
            return Task.CompletedTask;
        });
        var ctx = ProtocolJsonContext.Default;
        _rpc.Register(Methods.SessionOpen, ctx.SessionRef, ctx.Ok, async (p, _) =>
        {
            await _rpc.NotifyAsync(Notifications.SessionsChanged, new Empty(), ctx.Empty);
            return p.SessionId == "fail" ? throw new RpcException(RpcErrorCodes.InvalidParams, "nope") : new Ok();
        });
        _rpc.Register(Methods.AppInfo, ctx.Empty, ctx.Ok, (_, _) => Task.FromResult(new Ok()));
        _rpc.Register(Methods.SessionCancel, ctx.SessionRef, ctx.Ok, (_, _) => throw new InvalidOperationException("secret detail"));
    }

    private JsonElement Last() => _sent.Last();

    [Fact]
    public async Task Result_carries_the_request_id()
    {
        await _rpc.HandleAsync("""{"jsonrpc":"2.0","id":7,"method":"app.info","params":{}}""");

        Assert.Equal(7, Last().GetProperty("id").GetInt32());
        Assert.True(Last().GetProperty("result").GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Params_may_be_omitted_for_parameterless_methods()
    {
        await _rpc.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"app.info"}""");

        Assert.True(Last().TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Notifications_sent_by_a_handler_precede_its_result()
    {
        await _rpc.HandleAsync("""{"jsonrpc":"2.0","id":"a","method":"session.open","params":{"sessionId":"s"}}""");

        var messages = _sent.ToArray();
        Assert.Equal(Notifications.SessionsChanged, messages[0].GetProperty("method").GetString());
        Assert.Equal("a", messages[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Unknown_method_is_rejected()
    {
        await _rpc.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"shell.run","params":{"cmd":"rm -rf /"}}""");

        Assert.Equal(RpcErrorCodes.MethodNotFound, Last().GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Missing_required_params_are_invalid_params()
    {
        await _rpc.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"session.open","params":{}}""");

        Assert.Equal(RpcErrorCodes.InvalidParams, Last().GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Rpc_exceptions_keep_their_message_but_internal_errors_do_not_leak()
    {
        await _rpc.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"session.open","params":{"sessionId":"fail"}}""");
        Assert.Equal("nope", Last().GetProperty("error").GetProperty("message").GetString());

        await _rpc.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"session.cancel","params":{"sessionId":"x"}}""");
        Assert.Equal(RpcErrorCodes.InternalError, Last().GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("secret", Last().GetRawText());
    }

    [Fact]
    public async Task Malformed_json_is_a_parse_error()
    {
        await _rpc.HandleAsync("{not json");

        Assert.Equal(RpcErrorCodes.ParseError, Last().GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Notifications_get_no_reply()
    {
        await _rpc.HandleAsync("""{"jsonrpc":"2.0","method":"app.info"}""");

        Assert.Empty(_sent);
    }
}
