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

    /// <summary>The same question the fast loop asks every step and PcCheck asks on request.</summary>
    public static YesNoQuestion Achieved() => new(
        "The success criterion is already met on the current screen.",
        WhenYes: "the screen shows the success criterion is met",
        WhenNo: "the sub-goal is not achieved yet");

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

    public const string DecidableId = "decidable";

    /// <summary>
    /// Asked next to <see cref="Achieved"/> by PcCheck: can the element list say whether the criterion holds at all? A window that shows
    /// little of its content to UI Automation (a messenger's chat, a picture) answers "no" to every question about that content, and a
    /// "no" that means "I cannot see it" must not be read as "it did not happen".
    /// </summary>
    public static YesNoQuestion Decidable() => new(
        "The list of elements on this screen contains enough information to tell whether the success criterion is met or not, " +
        "either way. It is NOT enough when the criterion is about content the list does not show (a message that was sent, a picture, a video playing).",
        WhenYes: "the list shows what is needed to decide, yes or no",
        WhenNo: "the list does not show what the criterion is about");
}
