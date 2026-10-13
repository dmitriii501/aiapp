using Kairo.Core.Models;

namespace Kairo.Core.Agents;

public static class HistoryRepair
{
    /// <summary>
    /// Makes a loaded history valid for providers: if the session ended (crash, kill) after the model asked for
    /// tools but before their results were saved, answers those calls as interrupted. Only appends — never edits.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Repair(IReadOnlyList<ChatMessage> history)
    {
        if (history.Count == 0 || history[^1].Role != ChatRole.Assistant)
            return history;

        var calls = history[^1].Parts.OfType<ToolCallPart>().ToList();
        if (calls.Count == 0)
            return history;

        var results = calls.Select(c => (MessagePart)new ToolResultPart(c.Id, "Interrupted: the session ended before this ran.", IsError: true)).ToList();
        return [.. history, new ChatMessage(ChatRole.User, results)];
    }
}
