using Kairo.Core.Agents;
using Kairo.Core.Checkpoints;
using Kairo.Core.Models;
using Kairo.Core.Tools;
using Kairo.Protocol;

namespace Kairo.Host;

/// <summary>Turns a stored history into the rows the UI renders. Thinking is not shown.</summary>
public static class Transcript
{
    public const int MaxOutputChars = 4000;

    /// <summary>A message the user typed (as opposed to tool results the agent loop sends back as "user" messages).</summary>
    public static bool IsPrompt(ChatMessage message) => message.Role == ChatRole.User && message.UserTypedText.Length > 0;

    /// <param name="checkpoints">Checkpoints of the session by turn (1-based position of the prompt); shown on the prompt that caused them.</param>
    public static IReadOnlyList<TranscriptItem> Build(IReadOnlyList<ChatMessage> history, ToolRegistry tools, ToolContext ctx,
        IReadOnlyDictionary<int, CheckpointInfo>? checkpoints = null)
    {
        var items = new List<TranscriptItem>();
        var toolRows = new Dictionary<string, int>();
        var turn = 0;

        foreach (var message in history)
        {
            if (message.Role == ChatRole.User)
            {
                foreach (var result in message.Parts.OfType<ToolResultPart>())
                {
                    if (toolRows.TryGetValue(result.ToolCallId, out var row))
                        items[row] = items[row] with { Output = Clip(result.Content), Status = StatusOf(result.Content, result.IsError), Diff = result.Diff is { Length: > 0 } ? result.Diff : null };
                }
                if (IsPrompt(message))
                {
                    turn++;
                    CheckpointInfo? checkpoint = null;
                    checkpoints?.TryGetValue(turn, out checkpoint);
                    items.Add(new TranscriptItem("user", Text: message.UserTypedText, CheckpointId: checkpoint?.Id, Files: checkpoint?.Files));
                }
                continue;
            }

            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextPart t when t.Text.Length > 0:
                        items.Add(new TranscriptItem("assistant", Text: t.Text));
                        break;
                    case ToolCallPart c:
                        toolRows[c.Id] = items.Count;
                        items.Add(new TranscriptItem("tool", ToolCallId: c.Id, Tool: c.Name, Summary: Summarize(c, tools, ctx), Status: "interrupted"));
                        break;
                }
            }
        }
        return items;
    }

    public static string StatusOf(string content, bool isError) =>
        !isError ? "done" : content.StartsWith("Permission denied", StringComparison.Ordinal) ? "denied" : "error";

    public static string Clip(string output) =>
        output.Length <= MaxOutputChars ? output : output[..MaxOutputChars] + "\n…";

    private static string Summarize(ToolCallPart call, ToolRegistry tools, ToolContext ctx)
    {
        if (!tools.TryGet(call.Name, out var tool))
            return call.Name;
        try
        {
            return tool.Assess(call.Input, ctx).Summary;
        }
        catch (ToolInputException)
        {
            return call.Name;
        }
    }
}
