using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Kairo.Core.Models;
using ChatMessage = Kairo.Core.Models.ChatMessage;
using ChatRole = Kairo.Core.Models.ChatRole;

namespace Kairo.Providers.Anthropic;

/// <summary>Claude via the official Anthropic C# SDK.</summary>
/// <remarks>The SDK (12.x) relies on reflection-based JSON and is not trim/AOT safe — see ADR-0001.</remarks>
public sealed class AnthropicProvider : IModelProvider
{
    public const string DefaultModel = "claude-opus-5-5";

    private readonly AnthropicClient _client;

    /// <summary>
    /// False for gateways and proxies (a custom base URL): they often reject newer API fields, so requests
    /// leave out the fallback beta, top-level cache control and eager input streaming.
    /// </summary>
    private readonly bool _officialApi;

    /// <summary>
    /// Gateways get two plain block-level cache breakpoints (last tool, last message block) instead of the top-level field. Gateways
    /// that forward the Anthropic format keep the prefix cache this way; ones that do not support it ignore the marker.
    /// </summary>
    private readonly bool _gatewayCache;

    /// <param name="baseUrl">An Anthropic-format endpoint other than api.anthropic.com, e.g. a gateway. A trailing /v1 is accepted.</param>
    /// <param name="promptCache">False stops block-level cache markers for a gateway that rejects them.</param>
    public AnthropicProvider(string apiKey, HttpClient? httpClient = null, string? baseUrl = null, bool promptCache = true)
    {
        _officialApi = string.IsNullOrWhiteSpace(baseUrl);
        _gatewayCache = !_officialApi && promptCache;
        var url = _officialApi ? null : NormalizeBaseUrl(baseUrl!);
        _client = (httpClient, url) switch
        {
            (null, null) => new AnthropicClient { ApiKey = apiKey },
            (null, _) => new AnthropicClient { ApiKey = apiKey, BaseUrl = url },
            (_, null) => new AnthropicClient { ApiKey = apiKey, HttpClient = httpClient },
            _ => new AnthropicClient { ApiKey = apiKey, HttpClient = httpClient, BaseUrl = url },
        };
    }

    /// <summary>The SDK appends /v1/messages itself, so "https://host/v1/" becomes "https://host".</summary>
    internal static string NormalizeBaseUrl(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
    }

    public string Id => "anthropic";

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Messages = request.Messages.Select((m, i) => ToParam(m, cache: _gatewayCache && i == request.Messages.Count - 1)).ToList(),
        };
        if (_officialApi)
        {
            parameters = parameters with
            {
                // The agent resends the whole history every call: cache it.
                CacheControl = new BetaCacheControlEphemeral(),
                // If the model declines for policy reasons, the API retries on its default fallback model.
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
            };
        }
        if (request.System is { } system)
            parameters = parameters with { System = system };
        if (request.Tools is { Count: > 0 } tools)
        {
            parameters = parameters with
            {
                Tools = tools.Select((t, i) => ToTool(t, _officialApi, cache: _gatewayCache && i == tools.Count - 1)).ToList(),
            };
        }
        if (request.Effort is { } effort)
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = ParseEffort(effort) } };

        var blocks = new SortedDictionary<long, BlockBuilder>();
        string? stopReason = null;
        long? inputTokens = null, outputTokens = null, cacheRead = null;

        await foreach (var ev in _client.Beta.Messages.CreateStreaming(parameters, ct).WithCancellation(ct))
        {
            if (ev.TryPickContentBlockStart(out var start))
            {
                blocks[start.Index] = BlockBuilder.From(start.ContentBlock);
            }
            else if (ev.TryPickContentBlockDelta(out var delta))
            {
                if (!blocks.TryGetValue(delta.Index, out var b))
                    continue;
                if (delta.Delta.TryPickText(out var text))
                {
                    b.Text.Append(text.Text);
                    yield return new TextDelta(text.Text);
                }
                else if (delta.Delta.TryPickInputJson(out var json))
                {
                    b.Text.Append(json.PartialJson);
                }
                else if (delta.Delta.TryPickThinking(out var thinking))
                {
                    b.Text.Append(thinking.Thinking);
                }
                else if (delta.Delta.TryPickSignature(out var signature))
                {
                    b.Signature = signature.Signature;
                }
            }
            else if (ev.TryPickStart(out var messageStart))
            {
                inputTokens = messageStart.Message.Usage.InputTokens;
                cacheRead = messageStart.Message.Usage.CacheReadInputTokens;
            }
            else if (ev.TryPickDelta(out var messageDelta))
            {
                stopReason = messageDelta.Delta.StopReason?.Raw();
                outputTokens = messageDelta.Usage.OutputTokens;
            }
        }

        var parts = blocks.Values.Select(b => b.Build()).OfType<MessagePart>().ToList();
        yield return new ModelResponse(new ChatMessage(ChatRole.Assistant, parts), stopReason, new Usage(inputTokens, outputTokens, cacheRead));
    }

    /// <summary>Accumulates one streamed content block.</summary>
    private sealed class BlockBuilder
    {
        public enum Kind { Text, Thinking, RedactedThinking, ToolUse, Other }

        public Kind Type { get; private init; }
        public string Id { get; private init; } = "";
        public string Name { get; private init; } = "";
        public string Data { get; private init; } = "";
        public string Signature { get; set; } = "";
        public StringBuilder Text { get; } = new();

        public static BlockBuilder From(ContentBlock block)
        {
            if (block.TryPickBetaText(out var text))
                return new BlockBuilder { Type = Kind.Text }.With(text.Text);
            if (block.TryPickBetaThinking(out var thinking))
                return new BlockBuilder { Type = Kind.Thinking, Signature = thinking.Signature }.With(thinking.Thinking);
            if (block.TryPickBetaRedactedThinking(out var redacted))
                return new BlockBuilder { Type = Kind.RedactedThinking, Data = redacted.Data };
            if (block.TryPickBetaToolUse(out var toolUse))
                return new BlockBuilder { Type = Kind.ToolUse, Id = toolUse.ID, Name = toolUse.Name };
            return new BlockBuilder { Type = Kind.Other };
        }

        private BlockBuilder With(string initial)
        {
            Text.Append(initial);
            return this;
        }

        public MessagePart? Build() => Type switch
        {
            Kind.Text => Text.Length > 0 ? new TextPart(Text.ToString()) : null,
            Kind.Thinking => new ThinkingPart(Text.ToString(), Signature),
            Kind.RedactedThinking => new RedactedThinkingPart(Data),
            // Eager input streaming hands us raw fragments: parse strictly, never trust a partial object.
            Kind.ToolUse => new ToolCallPart(Id, Name, InvalidToolInput.ParseOrWrap(Text.ToString())),
            _ => null,
        };
    }

    /// <param name="cache">Marks the message's last block as a cache breakpoint (the end of the reusable prefix).</param>
    private static BetaMessageParam ToParam(ChatMessage message, bool cache = false)
    {
        var content = new List<BetaContentBlockParam>();
        for (var i = 0; i < message.Parts.Count; i++)
        {
            var part = message.Parts[i];
            var mark = cache && i == message.Parts.Count - 1 ? new BetaCacheControlEphemeral() : null;
            switch (part)
            {
                case TextPart t:
                    content.Add(mark is null ? new BetaTextBlockParam { Text = t.Text } : new BetaTextBlockParam { Text = t.Text, CacheControl = mark });
                    break;
                case ThinkingPart t:
                    content.Add(new BetaThinkingBlockParam { Thinking = t.Thinking, Signature = t.Signature });
                    break;
                case RedactedThinkingPart r:
                    content.Add(new BetaRedactedThinkingBlockParam { Data = r.Data });
                    break;
                case ToolCallPart c:
                    var use = new BetaToolUseBlockParam { ID = c.Id, Name = c.Name, Input = ToDictionary(c.Input) };
                    content.Add(mark is null ? use : use with { CacheControl = mark });
                    break;
                case ToolResultPart r:
                    var result = new BetaToolResultBlockParam { ToolUseID = r.ToolCallId, Content = r.Content, IsError = r.IsError };
                    content.Add(mark is null ? result : result with { CacheControl = mark });
                    break;
            }
        }

        return new BetaMessageParam
        {
            Role = message.Role == ChatRole.User ? Role.User : Role.Assistant,
            Content = content,
        };
    }

    private static BetaToolUnion ToTool(ToolSpec spec, bool eagerInputStreaming, bool cache)
    {
        var schema = spec.InputSchema;
        var properties = new Dictionary<string, JsonElement>();
        if (schema.TryGetProperty("properties", out var props))
        {
            foreach (var p in props.EnumerateObject())
                properties[p.Name] = p.Value.Clone();
        }
        var required = schema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [];

        var tool = new BetaTool
        {
            Name = spec.Name,
            Description = spec.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
        if (cache)
            tool = tool with { CacheControl = new BetaCacheControlEphemeral() };
        // Stream large inputs (file contents) as they are generated; Kairo validates them itself.
        return eagerInputStreaming ? tool with { EagerInputStreaming = true } : tool;
    }

    private static Dictionary<string, JsonElement> ToDictionary(JsonElement input)
    {
        var result = new Dictionary<string, JsonElement>();
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in input.EnumerateObject())
                result[p.Name] = p.Value.Clone();
        }
        return result;
    }

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new ArgumentException($"Unknown effort '{effort}'. Use low, medium, high, xhigh or max."),
    };
}
