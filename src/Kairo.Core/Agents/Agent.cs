using System.Runtime.CompilerServices;
using System.Text.Json;
using Kairo.Core.Hooks;
using Kairo.Core.Models;
using Kairo.Core.Permissions;
using Kairo.Core.Tools;

namespace Kairo.Core.Agents;

public abstract record AgentEvent;

public sealed record AgentTextDelta(string Text) : AgentEvent;

public sealed record ToolCallStarted(string Id, string Tool, string Summary, string? Detail = null) : AgentEvent;

public sealed record ToolCallFinished(string Id, string Tool, ToolResult Result, bool Denied) : AgentEvent;

/// <summary>A hook stopped something or asked the model to carry on; the user should see why.</summary>
public sealed record HookNotice(string Text) : AgentEvent;

/// <summary>One model call finished.</summary>
public sealed record ModelCallCompleted(string? StopReason, Usage Usage) : AgentEvent;

/// <summary>The run is over: the model answered without tool calls, or a limit was hit.</summary>
public sealed record AgentFinished(string Reason) : AgentEvent;

/// <summary>The history is about to be replaced by a summary (a model call that takes a few seconds).</summary>
public sealed record ContextCompacting : AgentEvent;

/// <summary>The history was replaced by a summary. Sizes are estimates in tokens.</summary>
public sealed record ContextCompacted(long TokensBefore, long TokensAfter, string Summary) : AgentEvent;

public enum PermissionAnswer { Allow, AllowAlways, Deny }

public sealed record PermissionRequest(string Tool, ToolAssessment Call, string Reason, string? Diff = null);

/// <summary>How the host asks the user. CLI prompts in the terminal; the desktop app shows a native card.</summary>
public interface IPermissionPrompt
{
    Task<PermissionAnswer> AskAsync(PermissionRequest request, CancellationToken ct);
}

/// <param name="ContextWindow">Size of the model's context in tokens; 0 turns automatic compaction off.</param>
/// <param name="CompactAt">Share of the window at which the history is summarized.</param>
/// <param name="Subagent">This agent was started by the Task tool: it does not offer subagents of its own.</param>
/// <param name="MaxModelCalls">Most model calls in one turn; 0 (the default) means no limit. Subagents and swarm workers set their own.</param>
public sealed record AgentOptions(string Model, string? Effort = null, int MaxModelCalls = 0, int ContextWindow = 0, double CompactAt = 0.8, bool Subagent = false);

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var t in tools)
            _tools.Add(t.Spec.Name, t);
    }

    public IReadOnlyList<ToolSpec> Specs => _tools.Values.Select(t => t.Spec).ToList();

    public IReadOnlyCollection<ITool> All => _tools.Values;

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

    /// <summary>The user's hooks (from config.toml); null when there are none.</summary>
    public IHooks? Hooks { get; init; }

    /// <summary>How many times a Stop hook may send the model back to work in one run.</summary>
    private const int MaxStopContinuations = 2;

    /// <summary>Context size reported by the model's last call (tokens): a better guide than the estimate.</summary>
    private long _lastContextTokens;

    /// <summary>Automatic compactions per run: a window too small for even one round must not loop.</summary>
    private const int MaxAutoCompactionsPerRun = 2;

    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>Raised for every message added to the history, e.g. to persist the session.</summary>
    public event Action<ChatMessage>? MessageAppended;

    /// <param name="note">Context from Kairo (not from the user), sent with the prompt and hidden in the transcript.</param>
    public async IAsyncEnumerable<AgentEvent> RunAsync(string userText, string? note = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!options.Subagent)
            toolContext.Subagents = new SubagentRunner(provider, tools, permissions, prompt, toolContext, systemPrompt, options, Hooks);

        var compactions = 0;
        if (ShouldCompact(compactions))
        {
            compactions++;
            yield return new ContextCompacting();
            yield return await CompactAsync(null, ct).ConfigureAwait(false);
        }

        if (!options.Subagent && Hooks is { } promptHooks && promptHooks.Has(HookEvent.UserPromptSubmit))
        {
            var outcome = await promptHooks.RunAsync(new HookInput(HookEvent.UserPromptSubmit, toolContext.WorkingDirectory, Prompt: userText), ct).ConfigureAwait(false);
            if (outcome.Blocked)
            {
                yield return new HookNotice($"The prompt was stopped by a hook: {outcome.Message}");
                yield return new AgentFinished("hook_blocked");
                yield break;
            }
            if (outcome.Context is { Length: > 0 } extra)
                note = note is { Length: > 0 } ? note + "\n\n" + extra : extra;
        }

        Append(note is { Length: > 0 }
            ? new ChatMessage(ChatRole.User, [new TextPart(ChatMessage.NoteOpen + note + ChatMessage.NoteClose + "\n\n"), new TextPart(userText)])
            : ChatMessage.UserText(userText));
        var stopContinuations = 0;

        for (var call = 0; call < (options.MaxModelCalls > 0 ? options.MaxModelCalls : int.MaxValue); call++)
        {
            // Long single turns (many tool calls) can fill the window too: summarize between model calls.
            if (call > 0 && ShouldCompact(compactions))
            {
                compactions++;
                yield return new ContextCompacting();
                yield return await CompactAsync(null, ct).ConfigureAwait(false);
            }

            ModelResponse? response = null;
            var request = new ModelRequest(options.Model, ModelImages.ForRequest(_history), systemPrompt, tools.Specs, Effort: options.Effort);

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

            _lastContextTokens = (response.Usage.InputTokens ?? 0) + (response.Usage.CacheReadTokens ?? 0);
            Append(response.Message);
            yield return new ModelCallCompleted(response.StopReason, response.Usage);

            var calls = response.Message.Parts.OfType<ToolCallPart>().ToList();
            if (calls.Count == 0)
            {
                // A Stop hook can send the model back to work, e.g. when the tests it runs are still failing.
                if (!options.Subagent && stopContinuations < MaxStopContinuations && Hooks is { } stopHooks && stopHooks.Has(HookEvent.Stop)
                    && response.StopReason is null or "end_turn" or "stop")
                {
                    var outcome = await stopHooks.RunAsync(new HookInput(HookEvent.Stop, toolContext.WorkingDirectory), ct).ConfigureAwait(false);
                    if (outcome.Blocked)
                    {
                        stopContinuations++;
                        Append(new ChatMessage(ChatRole.User, [new TextPart(ChatMessage.NoteOpen + "A hook asks you to continue: " + outcome.Message + ChatMessage.NoteClose)]));
                        yield return new HookNotice($"A hook asked the agent to continue: {outcome.Message}");
                        continue;
                    }
                }
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
                    yield return new ToolCallStarted(c.Id, c.Name, prepared.Summary, prepared.Assessment?.Detail);

                    var (result, denied) = prepared.Error is { } error
                        ? (error, false)
                        : await ExecuteAsync(c, prepared.Tool!, prepared.Assessment!, ct).ConfigureAwait(false);

                    results.Add(new ToolResultPart(c.Id, result.Content, result.IsError, result.Diff, result.Images));
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

    private bool ShouldCompact(int doneThisRun) =>
        options.ContextWindow > 0 && doneThisRun < MaxAutoCompactionsPerRun && _history.Count > 1
        && Math.Max(_lastContextTokens, Compaction.EstimateTokens(_history, systemPrompt)) >= options.ContextWindow * options.CompactAt;

    /// <summary>
    /// Replaces the history with a model-written summary (the stored log keeps everything; see <see cref="Compaction"/>).
    /// Never call it while a turn is running.
    /// </summary>
    public async Task<ContextCompacted> CompactAsync(string? focus, CancellationToken ct = default)
    {
        var before = Compaction.EstimateTokens(_history, systemPrompt);
        var summary = await Compaction.SummarizeAsync(provider, options.Model, _history, focus, ct).ConfigureAwait(false);
        _history.Clear();
        Append(summary);
        _lastContextTokens = 0;
        return new ContextCompacted(before, Compaction.EstimateTokens(_history, systemPrompt), Compaction.SummaryText(summary));
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
        // PreToolUse hooks run before the permission check: a hook can only make a call stricter, never allow what the rules deny.
        if (Hooks is { } pre && pre.Has(HookEvent.PreToolUse))
        {
            var outcome = await pre.RunAsync(new HookInput(HookEvent.PreToolUse, toolContext.WorkingDirectory, call.Name, call.Input), ct).ConfigureAwait(false);
            if (outcome.Blocked)
                return (ToolResult.Error($"Blocked by a hook: {outcome.Message}"), true);
        }

        var (result, denied) = await CheckAndRunAsync(call, tool, assessment, ct).ConfigureAwait(false);

        if (!denied && Hooks is { } post && post.Has(HookEvent.PostToolUse))
        {
            var outcome = await post.RunAsync(new HookInput(HookEvent.PostToolUse, toolContext.WorkingDirectory, call.Name, call.Input, result.Content), ct).ConfigureAwait(false);
            if (outcome.Blocked)
                result = result with { Content = result.Content + "\n\n[hook] " + outcome.Message };
        }
        return (result, denied);
    }

    private async Task<(ToolResult Result, bool Denied)> CheckAndRunAsync(ToolCallPart call, ITool tool, ToolAssessment assessment, CancellationToken ct)
    {
        var verdict = permissions.Evaluate(call.Name, assessment, toolContext);

        if (verdict.Decision == PermissionDecision.Ask)
        {
            var answer = await prompt.AskAsync(new PermissionRequest(call.Name, assessment, verdict.Reason, PreviewOf(tool, call)), ct).ConfigureAwait(false);
            // Approving a plan leaves plan mode: with edits auto-accepted ("always") or each one confirmed.
            if (call.Name == PermissionEngine.ExitPlanTool && answer != PermissionAnswer.Deny)
                permissions.Mode = answer == PermissionAnswer.AllowAlways ? PermissionMode.AcceptEdits : PermissionMode.Default;
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
