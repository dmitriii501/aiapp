namespace Kairo.Protocol;

public static partial class Methods
{
    public const string SwarmList = "swarm.list";
    public const string SwarmGet = "swarm.get";

    /// <summary>Starts a swarm in a known workspace. The UI sends the goal and limits; the host decides everything else.</summary>
    public const string SwarmStart = "swarm.start";

    /// <summary>Pauses a running swarm (its state is kept).</summary>
    public const string SwarmStop = "swarm.stop";

    /// <summary>Continues a paused or stalled swarm, optionally with a bigger token budget.</summary>
    public const string SwarmResume = "swarm.resume";
}

public static partial class Notifications
{
    /// <summary><see cref="SwarmDetail"/> whenever a swarm's state changes (throttled).</summary>
    public const string SwarmChanged = "swarm.changed";
}

/// <summary>Status: running | paused | stalled | done | failed. Running is true while this app drives it.</summary>
public sealed record SwarmSummary(
    string Id,
    string Goal,
    string Status,
    string WorkingDirectory,
    long TokensUsed,
    long MaxTokens,
    string UpdatedAt,
    bool Running);

/// <summary>Role: lead | coder. Status: idle | working | error.</summary>
public sealed record SwarmAgentDto(string Name, string Role, string Status, string? Branch, string WorkingDirectory);

/// <summary>Status: pending | in_progress | done | failed.</summary>
public sealed record SwarmTaskDto(int Seq, string Title, string Description, string Status, string? Assignee, IReadOnlyList<int> DependsOn, string? Result);

public sealed record SwarmMessageDto(long Id, string From, string To, string Text, string At);

/// <summary>Kind: spawn | status | tool | said | limit | end.</summary>
public sealed record SwarmLogDto(long Seq, string Kind, string Agent, string Text);

public sealed record SwarmDetail(
    SwarmSummary Summary,
    int MaxAgents,
    int MaxMinutes,
    int MaxConcurrency,
    int ModelCalls,
    string? Report,
    IReadOnlyList<SwarmAgentDto> Agents,
    IReadOnlyList<SwarmTaskDto> Tasks,
    IReadOnlyList<SwarmMessageDto> Messages,
    IReadOnlyList<SwarmLogDto> Log,
    IReadOnlyList<PermissionPrompt> PendingPermissions);

public sealed record SwarmListResult(IReadOnlyList<SwarmSummary> Swarms);

public sealed record SwarmIdParams(string SwarmId);

/// <summary>Workers is the most workers besides the lead; null keeps the default.</summary>
public sealed record SwarmStartParams(string WorkingDirectory, string Goal, int? Workers = null, long? MaxTokens = null, int? MaxMinutes = null);

public sealed record SwarmResumeParams(string SwarmId, long? MaxTokens = null);
