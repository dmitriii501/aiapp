using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairo.Core.Decisions;

// A decision model (docs/specs/pc-control.md → "Абстракция: модели решений") does not write text: it picks one of the options
// the core offers, rates something on a scale, or says how likely a yes is. The core builds every option list and checks what comes
// back, so the model can only ever choose something that was already allowed.

/// <summary>One thing to decide. All questions of a step go to the model in a single request and are answered together.</summary>
public abstract record DecisionQuestion(string Instructions);

/// <summary>Pick one of <see cref="Options"/> (key → what it means). Order is kept; at most 255.</summary>
public sealed record ChoiceQuestion(string Instructions, IReadOnlyList<ChoiceOption> Options) : DecisionQuestion(Instructions);

public sealed record ChoiceOption(string Key, string? Description = null);

/// <summary>Rate along ordered <see cref="Levels"/>, lowest first (2 to 10). The answer can fall between two levels.</summary>
public sealed record ScoreQuestion(string Instructions, IReadOnlyList<string> Levels) : DecisionQuestion(Instructions);

/// <summary>A yes/no question; the answer is the probability of yes. <paramref name="WhenYes"/> and <paramref name="WhenNo"/> say what each answer means.</summary>
public sealed record YesNoQuestion(string Instructions, string? WhenYes = null, string? WhenNo = null) : DecisionQuestion(Instructions);

/// <summary>What the questions are about, as text or JSON. Names the parts (an object) rather than one long string.</summary>
public sealed record DecisionState(JsonElement Value)
{
    public static DecisionState Text(string text) => new(JsonSerializer.SerializeToElement(text, DecisionJson.Default.String));

    public static DecisionState Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new DecisionState(doc.RootElement.Clone());
    }
}

public sealed record DecisionRequest(DecisionState State, IReadOnlyDictionary<string, DecisionQuestion> Questions)
{
    public static DecisionRequest Single(DecisionState state, string id, DecisionQuestion question) =>
        new(state, new Dictionary<string, DecisionQuestion> { [id] = question });
}

public abstract record DecisionAnswer;

/// <param name="Choice">One of the option keys that were offered.</param>
/// <param name="Confidence">0 to 1: how sure the model is of this choice.</param>
public sealed record ChoiceAnswer(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) : DecisionAnswer;

/// <param name="Score">Position along the levels, 0 for the first level, <c>Levels.Count - 1</c> for the last.</param>
public sealed record ScoreAnswer(double Score, double Confidence, IReadOnlyDictionary<string, double> Probabilities) : DecisionAnswer;

/// <param name="Probability">Probability of yes, 0 to 1. Yes/no answers have no separate confidence: the probability is it.</param>
public sealed record YesNoAnswer(double Probability) : DecisionAnswer;

/// <param name="Model">The versioned model that answered (an alias can move to a new release).</param>
/// <param name="Elapsed">Wall time of the request, for the latency numbers the policy and the settings show.</param>
public sealed record DecisionResult(string Model, IReadOnlyDictionary<string, DecisionAnswer> Answers, long InputTokens, TimeSpan Elapsed)
{
    public ChoiceAnswer Choice(string id) => Get<ChoiceAnswer>(id);

    public ScoreAnswer Score(string id) => Get<ScoreAnswer>(id);

    public YesNoAnswer YesNo(string id) => Get<YesNoAnswer>(id);

    private T Get<T>(string id) where T : DecisionAnswer =>
        Answers.TryGetValue(id, out var a) && a is T typed ? typed : throw new DecisionException($"The answer '{id}' is missing or of another kind.");
}

public interface IDecisionModel
{
    string Id { get; }

    /// <summary>Answers all questions of <paramref name="request"/> in one call. Throws <see cref="DecisionException"/> when it cannot.</summary>
    Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct);
}

/// <summary>A decision model that can open its connection ahead of the first question.</summary>
public interface IWarmUp
{
    Task WarmUpAsync(CancellationToken ct = default);
}

public static class DecisionModelExtensions
{
    public static async Task<ChoiceAnswer> ChooseAsync(this IDecisionModel model, DecisionState state, ChoiceQuestion question, CancellationToken ct) =>
        (await model.DecideAsync(DecisionRequest.Single(state, "q", question), ct).ConfigureAwait(false)).Choice("q");

    public static async Task<ScoreAnswer> ScoreAsync(this IDecisionModel model, DecisionState state, ScoreQuestion question, CancellationToken ct) =>
        (await model.DecideAsync(DecisionRequest.Single(state, "q", question), ct).ConfigureAwait(false)).Score("q");

    public static async Task<double> YesAsync(this IDecisionModel model, DecisionState state, string question, CancellationToken ct) =>
        (await model.DecideAsync(DecisionRequest.Single(state, "q", new YesNoQuestion(question)), ct).ConfigureAwait(false)).YesNo("q").Probability;
}

/// <summary>Why a decision could not be made. <see cref="Kind"/> tells the caller whether to retry, escalate or ask the user.</summary>
public enum DecisionFailure
{
    /// <summary>The question itself is wrong (too many options, too long): a bug or a state that needs shrinking.</summary>
    Invalid,
    /// <summary>No key, or the key was refused.</summary>
    Unauthorized,
    /// <summary>Rate limit or overload that did not clear within the time allowed.</summary>
    Busy,
    /// <summary>No answer in time.</summary>
    Timeout,
    /// <summary>The service or the network failed, or the reply was not what the contract says.</summary>
    Unavailable,
}

public sealed class DecisionException(string message, DecisionFailure kind = DecisionFailure.Unavailable, Exception? inner = null)
    : Exception(message, inner)
{
    public DecisionFailure Kind { get; } = kind;
}

/// <summary>Source-generated JSON metadata so the state helpers stay reflection-free.</summary>
[JsonSerializable(typeof(string))]
internal sealed partial class DecisionJson : JsonSerializerContext;
