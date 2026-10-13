using System.Runtime.CompilerServices;
using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Tools;

namespace Kairo.Core.Agents;

public abstract record AgentEvent;

public sealed record AgentTextDelta(string Text) : AgentEvent;

public sealed record ToolCallStarted(string Id, string Tool, string Summary) : AgentEvent;

public sealed record ToolCallFinished(string Id, string Tool, ToolResult Result, bool Denied) : AgentEvent;

/// <summary>One model call finished.</summary>
public sealed record ModelCallCompleted(string? StopReason, Usage Usage) : AgentEvent;

/// <summary>The run is over: the model answered without tool calls, or a limit was hit.</summary>
public sealed record AgentFinished(string Reason) : AgentEvent;

public enum PermissionAnswer { Allow, AllowAlways, Deny }

public sealed record PermissionRequest(string Tool, ToolAssessment Call, string Reason, string? Diff = null);

/// <summary>How the host asks the user. CLI prompts in the terminal; the desktop app shows a native card.</summary>
public interface IPermissionPrompt
{
    Task<PermissionAnswer> AskAsync(PermissionRequest request, CancellationToken ct);
}

public sealed record AgentOptions(string Model, string? Effort = null, int MaxModelCalls = 60);

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var t in tools)
            _tools.Add(t.Spec.Name, t);
    }

    public IReadOnlyList<ToolSpec> Specs => _tools.Values.Select(t => t.Spec).ToList();

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);
}

/// <summary>
/// The agent loop: prompt → model → tool calls → permissions → execution → results → model …
/// History is append-only, and every tool call gets exactly one result, even on denial or cancellation,
/// because providers reject a tool call without a matching result.
/// </summary>
public sealed class Agent(
    IModelProvider provider,
    ToolRegistry tools,
    PermissionEngine permissions,
    IPermissionPrompt prompt,
    ToolContext toolContext,
    string systemPrompt,
    AgentOptions options,
    IEnumerable<ChatMessage>? history = null)
{
    private readonly List<ChatMessage> _history = [.. history ?? []];

    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>Raised for every message added to the history, e.g. to persist the session.</summary>
    public event Action<ChatMessage>? MessageAppended;

    /// <param name="note">Context from Kairo (not from the user), sent with the prompt and hidden in the transcript.</param>
    public async IAsyncEnumerable<AgentEvent> RunAsync(string userText, string? note = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Append(note is { Length: > 0 }
            ? new ChatMessage(ChatRole.User, [new TextPart(ChatMessage.NoteOpen + note + ChatMessage.NoteClose + "\n\n"), new TextPart(userText)])
            : ChatMessage.UserText(userText));

        for (var call = 0; call < options.MaxModelCalls; call++)
        {
            ModelResponse? response = null;
            var request = new ModelRequest(options.Model, _history, systemPrompt, tools.Specs, Effort: options.Effort);

            await foreach (var ev in provider.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case TextDelta d:
                        yield return new AgentTextDelta(d.Text);
                        break;
                    case ModelResponse r:
                        response = r;
                        break;
                }
            }

            if (response is null)
                throw new ModelProviderException("The model stream ended without a response.");

            Append(response.Message);
            yield return new ModelCallCompleted(response.StopReason, response.Usage);

            var calls = response.Message.Parts.OfType<ToolCallPart>().ToList();
            if (calls.Count == 0)
            {
                yield return new AgentFinished(response.StopReason ?? "end_turn");
                yield break;
            }

            // A cut-off response may carry a truncated tool input: never run it.
            if (response.StopReason is "max_tokens" or "refusal")
            {
                var reason = $"Not executed: the response stopped early (stop_reason={response.StopReason}).";
                Append(new ChatMessage(ChatRole.User, calls.Select(c => (MessagePart)new ToolResultPart(c.Id, reason, IsError: true)).ToList()));
                if (response.StopReason == "refusal")
                {
                    yield return new AgentFinished("refusal");
                    yield break;
                }
                continue;
            }

            var results = new List<MessagePart>(calls.Count);
            try
            {
                foreach (var c in calls)
                {
                    var prepared = Prepare(c);
                    yield return new ToolCallStarted(c.Id, c.Name, prepared.Summary);

                    var (result, denied) = prepared.Error is { } error
                        ? (error, false)
                        : await ExecuteAsync(c, prepared.Tool!, prepared.Assessment!, ct).ConfigureAwait(false);

                    results.Add(new ToolResultPart(c.Id, result.Content, result.IsError, result.Diff));
                    yield return new ToolCallFinished(c.Id, c.Name, result, denied);
                }
            }
            finally
            {
                // On cancellation, answer the remaining calls so the history stays valid for the next turn.
                foreach (var c in calls.Skip(results.Count))
                    results.Add(new ToolResultPart(c.Id, "Interrupted by the user.", IsError: true));
                Append(new ChatMessage(ChatRole.User, results));
            }
        }

        yield return new AgentFinished("max_model_calls");
    }

    private sealed record Prepared(string Summary, ITool? Tool = null, ToolAssessment? Assessment = null, ToolResult? Error = null);

    private Prepared Prepare(ToolCallPart call)
    {
        if (!tools.TryGet(call.Name, out var tool))
            return new Prepared(call.Name, Error: ToolResult.Error($"Unknown tool '{call.Name}'."));

        if (InvalidToolInput.TryUnwrap(call.Input, out var raw))
            return new Prepared(call.Name, Error: ToolResult.Error(InvalidJsonMessage(raw)));

        try
        {
            var assessment = tool.Assess(call.Input, toolContext);
            return new Prepared(assessment.Summary, tool, assessment);
        }
        catch (ToolInputException ex)
        {
            return new Prepared(call.Name, Error: ToolResult.Error(ex.Message));
        }
    }

    private async Task<(ToolResult Result, bool Denied)> ExecuteAsync(ToolCallPart call, ITool tool, ToolAssessment assessment, CancellationToken ct)
    {
        var verdict = permissions.Evaluate(call.Name, assessment, toolContext);

        if (verdict.Decision == PermissionDecision.Ask)
        {
            var answer = await prompt.AskAsync(new PermissionRequest(call.Name, assessment, verdict.Reason, PreviewOf(tool, call)), ct).ConfigureAwait(false);
            if (answer == PermissionAnswer.AllowAlways)
                permissions.AllowForSession(call.Name, assessment);
            verdict = answer == PermissionAnswer.Deny
                ? new PermissionVerdict(PermissionDecision.Deny, "the user declined")
                : new PermissionVerdict(PermissionDecision.Allow, "the user approved");
        }

        if (verdict.Decision == PermissionDecision.Deny)
            return (ToolResult.Error($"Permission denied ({verdict.Reason}). Do not retry this action in another form."), true);

        try
        {
            return (await tool.InvokeAsync(call.Input, toolContext, ct).ConfigureAwait(false), false);
        }
        catch (ToolInputException ex)
        {
            return (ToolResult.Error(ex.Message), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (ToolResult.Error($"{call.Name} failed: {ex.Message}"), false);
        }
    }

    private string? PreviewOf(ITool tool, ToolCallPart call)
    {
        try
        {
            return tool.Preview(call.Input, toolContext);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolInputException)
        {
            return null;
        }
    }

    private static string InvalidJsonMessage(string raw)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("INVALID_JSON", raw);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void Append(ChatMessage message)
    {
        _history.Add(message);
        MessageAppended?.Invoke(message);
    }
}
