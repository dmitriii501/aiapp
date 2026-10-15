namespace Kairo.Core.Agents;

public enum GoalStatus
{
    /// <summary>Kairo keeps the agent working: when a turn ends and the goal is not done, the next one starts by itself.</summary>
    Active,
    /// <summary>The model called GoalComplete with its evidence.</summary>
    Complete,
    /// <summary>The model said it cannot go on without the user (GoalBlocked).</summary>
    Blocked,
    /// <summary>Stopped by the user, by an error that kept coming back, or because the model stopped making progress. /goal resume continues.</summary>
    Paused,
}

/// <summary>
/// What the user asked the agent to reach with <c>/goal</c>. While it is <see cref="GoalStatus.Active"/>, the host starts a new turn
/// whenever one ends without the goal being reached, so the agent does not just stop in the middle of a long job.
/// </summary>
public sealed class GoalState(string text, GoalStatus status = GoalStatus.Active, string? detail = null)
{
    /// <summary>Turns started by Kairo itself since the goal was set (not the user's own messages).</summary>
    public const int MaxContinuations = 500;

    public string Text { get; } = text;
    public GoalStatus Status { get; set; } = status;

    /// <summary>The evidence of GoalComplete, the reason of GoalBlocked, or why the goal was paused.</summary>
    public string? Detail { get; set; } = detail;

    public int Continuations { get; set; }

    /// <summary>Turns in a row that ended without a single tool call: the model is talking, not working.</summary>
    public int IdleTurns { get; set; }

    /// <summary>Turns in a row that failed (a provider error); a good turn resets it.</summary>
    public int ErrorsInRow { get; set; }

    /// <summary>Hidden instructions sent with every turn of an active goal.</summary>
    public string Instructions() =>
        $"A goal is active: \"{Text}\". Keep working until it is completely achieved and you have checked the result yourself " +
        "(run it, read the file, look at the screen). Do not stop to ask whether to go on, and do not end a turn with a progress report: " +
        "if there is more to do, do it with tools in this same turn. When the goal is truly achieved, call GoalComplete with the evidence " +
        "(what you checked and what you saw). If you cannot go on without the user (a decision only they can make, a missing password or file, " +
        "a permission they refused), call GoalBlocked and say what you need. Never call GoalComplete only to end the turn.";
}
