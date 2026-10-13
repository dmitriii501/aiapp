using System.Text;
using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kairo.Mcp;

/// <summary>
/// A tool of an MCP server, as the agent sees it. Always <see cref="RiskLevel.Exec"/>: a server is code we did not
/// write, and its own hints (such as "read only") are not trusted. Allow rules like <c>mcp__server__*</c> relax that.
/// </summary>
internal sealed class McpTool : ITool
{
    private const int MaxResultChars = 30_000;
    private const int MaxSummaryArgsChars = 80;

    private readonly McpClient _client;
    private readonly string _remoteName;

    public McpTool(string exposedName, string serverName, McpClient client, Tool tool)
    {
        _client = client;
        _remoteName = tool.Name;
        ServerName = serverName;

        var description = string.IsNullOrWhiteSpace(tool.Description) ? tool.Title ?? tool.Name : tool.Description!;
        Spec = new ToolSpec(exposedName, $"[MCP: {serverName}] {description}", ObjectSchema(tool.InputSchema));
    }

    public string ServerName { get; }

    public ToolSpec Spec { get; }

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var args = Compact(input);
        var shown = args.Length <= MaxSummaryArgsChars ? args : args[..MaxSummaryArgsChars] + "…";
        return new(RiskLevel.Exec, Pretty(input), $"{Spec.Name}({shown})");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (input.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in input.EnumerateObject())
                arguments[property.Name] = property.Value.Clone();
        }

        var result = await _client.CallToolAsync(_remoteName, arguments, cancellationToken: ct).ConfigureAwait(false);
        var text = Render(result);
        return result.IsError == true ? ToolResult.Error(text) : new ToolResult(text);
    }

    internal static string Render(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(block switch
            {
                TextContentBlock t => t.Text,
                ImageContentBlock i => $"[image {i.MimeType}, not shown]",
                AudioContentBlock a => $"[audio {a.MimeType}, not shown]",
                ResourceLinkBlock l => $"[resource {l.Uri}]",
                EmbeddedResourceBlock { Resource: TextResourceContents r } => r.Text,
                EmbeddedResourceBlock => "[binary resource, not shown]",
                _ => "[unsupported content]",
            });
        }
        if (sb.Length == 0 && result.StructuredContent is { } structured)
            sb.Append(structured.GetRawText());
        if (sb.Length == 0)
            sb.Append("(no output)");

        return sb.Length <= MaxResultChars ? sb.ToString() : sb.ToString(0, MaxResultChars) + $"\n[output truncated: {sb.Length - MaxResultChars} more characters]";
    }

    /// <summary>Providers require the schema root to be an object; some servers omit the type.</summary>
    private static JsonElement ObjectSchema(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
            return schema.Clone();

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "object");
            if (schema.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in schema.EnumerateObject().Where(p => p.Name != "type"))
                    p.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static string Compact(JsonElement input) => input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();

    private static string Pretty(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return "{}";
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            input.WriteTo(w);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
