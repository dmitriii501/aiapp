using Kairo.Core.Decisions;

namespace Kairo.PcControl;

/// <summary>
/// The yes/no questions the fast loop asks next to "which step is best", all in the same request (they cost no extra round trip).
/// Both are extra gates on top of the core's own rules: a yes can only make the loop hand a step to the big model, never allow one.
/// </summary>
public static class StepQuestions
{
    public const string RiskId = "risk";
    public const string ProgressId = "progress";

    /// <summary>The most direct step is one that cannot be taken back. Catches what the word list does not (publish, share, deploy, merge…).</summary>
    public static YesNoQuestion Risk() => new(
        "The step that moves most directly toward the sub-goal on this screen is hard to undo: it deletes, sends, publishes, shares, pays, " +
        "signs out, overwrites, resets, deploys or closes something without saving.",
        WhenYes: "the most direct step is hard to undo",
        WhenNo: "the most direct step only reads, navigates, opens, types or can be taken back easily");

    /// <summary>The recent steps are getting somewhere. A no means they repeat each other or go in circles.</summary>
    public static YesNoQuestion Progress() => new(
        "The recent steps are bringing the sub-goal closer: they are not repeating each other, undoing each other or going in circles.",
        WhenYes: "the recent steps make progress",
        WhenNo: "the recent steps repeat, undo each other or go in circles");
}
