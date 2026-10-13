using System.Runtime.CompilerServices;
using System.Text.Json;
using Kairo.Core.Agents;
using Kairo.Core.Models;

namespace Kairo.Core.Tests.Fakes;

/// <summary>A model that replays scripted responses and records every request it receives.</summary>
internal sealed class ScriptedProvider(params ModelResponse[] script) : IModelProvider
{
    private readonly Queue<ModelResponse> _script = new(script);

    public List<ModelRequest> Requests { get; } = [];

    public string Id => "scripted";

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Snapshot: the agent's history list keeps growing after the call.
        Requests.Add(request with { Messages = request.Messages.ToList() });
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        var next = _script.Count > 0 ? _script.Dequeue() : Say("(script exhausted)");
        foreach (var t in next.Message.Parts.OfType<TextPart>())
            yield return new TextDelta(t.Text);
        yield return next;
    }

    public static ModelResponse Say(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextPart(text)]), "end_turn", new Usage(10, 5));

    public static ModelResponse Call(string id, string tool, string inputJson, string stopReason = "tool_use") =>
        new(new ChatMessage(ChatRole.Assistant, [new ToolCallPart(id, tool, InvalidToolInput.ParseOrWrap(inputJson))]), stopReason, new Usage(10, 5));
}

internal sealed class FakePrompt(PermissionAnswer answer) : IPermissionPrompt
{
    public List<PermissionRequest> Asked { get; } = [];

    public Task<PermissionAnswer> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        Asked.Add(request);
        return Task.FromResult(answer);
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kairo-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class Json
{
    public static JsonElement Of(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>JSON-escapes a string (e.g. a Windows path) for use inside a JSON literal.</summary>
    public static string Str(string s) => JsonSerializer.Serialize(s, JsonContext.Default.String)[1..^1];
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext;
