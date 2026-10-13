using System.Text;
using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.Swarm;

/// <summary>What the swarm tools need from the runner.</summary>
public interface ISwarmHost
{
    string SwarmId { get; }

    string MainDirectory { get; }

    SwarmStore Store { get; }

    /// <summary>Starts a worker with its own worktree. Returns its name.</summary>
    Task<string> SpawnAsync(string role, string? name, string? brief, CancellationToken ct);

    void Finish(string summary);

    /// <summary>The state of the board or the messages changed: idle agents should look again.</summary>
    void Changed();
}

/// <summary>
/// Base of the tools a swarm agent gets. They report as reads (they touch the swarm's own state, not the user's files and
/// system), except merging, which changes the user's repository and asks like any command.
/// </summary>
public abstract class SwarmTool(ISwarmHost host, string agent) : ITool
{
    protected ISwarmHost Host { get; } = host;
    protected string Agent { get; } = agent;
    protected bool IsLead => Agent == SwarmRunner.LeadName;

    public abstract ToolSpec Spec { get; }

    public virtual ToolAssessment Assess(JsonElement input, ToolContext ctx) =>
        new(RiskLevel.Read, ctx.WorkingDirectory, Spec.Name);

    public abstract Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct);

    protected static int RequiredInt(JsonElement input, string name) =>
        ToolInput.OptionalInt(input, name) ?? throw new ToolInputException($"Missing required integer parameter '{name}'.");

    protected static IReadOnlyList<int> IntList(JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<int>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var n))
                throw new ToolInputException($"'{name}' must be a list of task numbers.");
            list.Add(n);
        }
        return list;
    }

    protected SwarmAgent? FindAgent(string name) => Host.Store.Agents(Host.SwarmId).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string FormatBoard(IReadOnlyList<SwarmTask> tasks)
    {
        if (tasks.Count == 0)
            return "(the board is empty)";
        // Plain LF line breaks: the board goes into prompts and tool results, where CRLF is noise.
        return string.Join('\n', tasks.Select(t =>
            $"#{t.Seq} [{t.Status}] {t.Title}"
            + (t.Assignee is not null ? $" — {t.Assignee}" : "")
            + (t.DependsOn.Count > 0 ? $" (after {string.Join(", ", t.DependsOn.Select(d => "#" + d))})" : "")));
    }
}

public sealed class TaskListTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public override ToolSpec Spec { get; } = new("task_list",
        "Shows the swarm's task board: every task with its number, status, owner and dependencies.",
        ToolInput.Schema());

    public override Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var tasks = Host.Store.Tasks(Host.SwarmId);
        var details = tasks.Where(t => t.Assignee == Agent || IsLead).Where(t => t.Description.Length > 0 || t.Result is not null).ToList();
        var sb = new StringBuilder(FormatBoard(tasks));
        foreach (var t in details)
        {
            sb.AppendLine().AppendLine().Append($"#{t.Seq} {t.Title}");
            if (t.Description.Length > 0)
                sb.AppendLine().Append(t.Description);
            if (t.Result is not null)
                sb.AppendLine().Append("Result: ").Append(t.Result);
        }
        return Task.FromResult(new ToolResult(sb.ToString()));
    }
}

public sealed class TaskClaimTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public override ToolSpec Spec { get; } = new("task_claim",
        "Takes a task for yourself. It must be pending, unassigned or assigned to you, and the tasks it depends on must be done. Taking is atomic: if someone else got it first you are told.",
        ToolInput.Schema(("id", "integer", "The task number.", true)));

    public override Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var seq = RequiredInt(input, "id");
        var problem = Host.Store.Claim(Host.SwarmId, seq, Agent);
        if (problem is not null)
            return Task.FromResult(ToolResult.Error(problem));
        Host.Changed();
        var task = Host.Store.GetTask(Host.SwarmId, seq)!;
        return Task.FromResult(new ToolResult($"You now hold task #{seq}: {task.Title}\n{task.Description}".TrimEnd()));
    }
}

public sealed class TaskUpdateTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public override ToolSpec Spec { get; } = new("task_update",
        "Reports progress on a task you hold. Status 'done' or 'failed' with a short result (what changed, how you checked it). " +
        "Marking a task done also commits your work in your git branch. Only the task's owner or the lead can update it.",
        ToolInput.Schema(
            ("id", "integer", "The task number.", true),
            ("status", "string", "'in_progress', 'done' or 'failed'.", true),
            ("result", "string", "What came of it. Required for done and failed.", false)));

    public override async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var seq = RequiredInt(input, "id");
        var status = ToolInput.RequiredString(input, "status").Trim();
        var result = ToolInput.OptionalString(input, "result")?.Trim();
        if (!TaskState.IsValid(status) || status == TaskState.Pending)
            throw new ToolInputException("status must be 'in_progress', 'done' or 'failed'.");
        if (status is TaskState.Done or TaskState.Failed && string.IsNullOrEmpty(result))
            throw new ToolInputException("A result is required when a task is done or failed.");

        var task = Host.Store.GetTask(Host.SwarmId, seq) ?? throw new ToolInputException($"There is no task {seq}.");
        if (!IsLead && task.Assignee != Agent)
            return ToolResult.Error($"Task #{seq} is not yours{(task.Assignee is null ? ": claim it first" : $" (it belongs to {task.Assignee})")}.");

        var note = "";
        if (status == TaskState.Done && !IsLead)
        {
            var committed = await GitWorkspace.CommitAllAsync(ctx.WorkingDirectory, $"kairo: task #{seq} {task.Title}", ct).ConfigureAwait(false);
            note = committed ? " Your changes are committed in your branch." : " There was nothing to commit.";
        }

        Host.Store.UpdateTask(Host.SwarmId, seq, status, result);
        Host.Changed();
        return new ToolResult($"Task #{seq} is now {status}.{note}");
    }
}

public sealed class TaskCreateTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public const int MaxTasks = 60;

    public override ToolSpec Spec { get; } = new("task_create",
        "Adds a task to the board (lead only). Make tasks small and self-contained: a worker sees only the title and description. " +
        "List the numbers of tasks that must be done first in depends_on; assign to a worker by name or leave it free for any worker to take.",
        CreateSchema());

    private static JsonElement CreateSchema()
    {
        var schema = ToolInput.Schema(
            ("title", "string", "Short title.", true),
            ("description", "string", "What to do, where, and how to tell it is done.", true),
            ("assignee", "string", "Worker name, or leave out.", false));
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var p in schema.EnumerateObject())
            {
                if (p.Name != "properties")
                {
                    p.WriteTo(w);
                    continue;
                }
                w.WriteStartObject("properties");
                foreach (var prop in p.Value.EnumerateObject())
                    prop.WriteTo(w);
                w.WriteStartObject("depends_on");
                w.WriteString("type", "array");
                w.WriteString("description", "Numbers of tasks that must be done before this one.");
                w.WriteStartObject("items");
                w.WriteString("type", "integer");
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    public override Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        if (!IsLead)
            return Task.FromResult(ToolResult.Error("Only the lead creates tasks. Ask the lead with send_message."));
        var title = ToolInput.RequiredString(input, "title").Trim();
        var description = ToolInput.RequiredString(input, "description").Trim();
        if (title.Length == 0)
            throw new ToolInputException("The title is empty.");
        var tasks = Host.Store.Tasks(Host.SwarmId);
        if (tasks.Count >= MaxTasks)
            return Task.FromResult(ToolResult.Error($"The board already has {MaxTasks} tasks."));

        var dependsOn = IntList(input, "depends_on");
        if (dependsOn.FirstOrDefault(d => tasks.All(t => t.Seq != d)) is var missing && missing != 0)
            throw new ToolInputException($"depends_on mentions task {missing}, which does not exist.");

        string? assignee = null;
        if (ToolInput.OptionalString(input, "assignee")?.Trim() is { Length: > 0 } requested)
        {
            assignee = (FindAgent(requested) ?? throw new ToolInputException($"There is no agent '{requested}'. Spawn it first or leave the task free.")).Name;
        }

        var task = Host.Store.CreateTask(Host.SwarmId, title, description, dependsOn, assignee);
        Host.Changed();
        return Task.FromResult(new ToolResult($"Created task #{task.Seq}: {task.Title}"));
    }
}

public sealed class SendMessageTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public const int MaxChars = 4000;

    public override ToolSpec Spec { get; } = new("send_message",
        "Sends a message to another agent (by name) or to everyone ('all'). It is delivered when the recipient starts its next turn. " +
        "Use it to ask the lead for information, to report a blocker, or to coordinate on shared files.",
        ToolInput.Schema(
            ("to", "string", "Agent name, e.g. 'lead' or 'coder-1', or 'all'.", true),
            ("text", "string", "The message.", true)));

    public override Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var to = ToolInput.RequiredString(input, "to").Trim();
        var text = ToolInput.RequiredString(input, "text").Trim();
        if (text.Length == 0)
            throw new ToolInputException("The message is empty.");
        if (text.Length > MaxChars)
            throw new ToolInputException($"The message is too long ({text.Length} characters, at most {MaxChars}). Put details in a file or the shared notes.");

        var agents = Host.Store.Agents(Host.SwarmId);
        var recipients = string.Equals(to, "all", StringComparison.OrdinalIgnoreCase)
            ? agents.Where(a => a.Name != Agent).Select(a => a.Name).ToList()
            : agents.Where(a => string.Equals(a.Name, to, StringComparison.OrdinalIgnoreCase) && a.Name != Agent).Select(a => a.Name).ToList();
        if (recipients.Count == 0)
            return Task.FromResult(ToolResult.Error($"Nobody to send to: there is no other agent '{to}'. Agents: {string.Join(", ", agents.Select(a => a.Name))}."));

        foreach (var r in recipients)
            Host.Store.Send(Host.SwarmId, Agent, r, text);
        Host.Changed();
        return Task.FromResult(new ToolResult($"Sent to {string.Join(", ", recipients)}."));
    }
}

public sealed class ReadInboxTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public override ToolSpec Spec { get; } = new("read_inbox", "Reads the messages other agents sent you since you last read.", ToolInput.Schema());

    public override Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var messages = Host.Store.TakeInbox(Host.SwarmId, Agent);
        return Task.FromResult(new ToolResult(messages.Count == 0 ? "No new messages." : FormatMessages(messages)));
    }

    public static string FormatMessages(IReadOnlyList<SwarmMessage> messages) =>
        string.Join("\n\n", messages.Select(m => $"<message from=\"{m.From}\">\n{m.Text}\n</message>"));
}

public sealed class MemoryReadTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public override ToolSpec Spec { get; } = new("memory_read",
        "Reads the swarm's shared notes: facts and decisions every agent can see (conventions, file locations, API shapes).", ToolInput.Schema());

    public override Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var notes = Host.Store.Notes(Host.SwarmId);
        return Task.FromResult(new ToolResult(notes.Count == 0 ? "No notes yet." : string.Join("\n", notes.Select(n => $"- ({n.Author}) {n.Text}"))));
    }
}

public sealed class MemoryWriteTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public const int MaxChars = 1000;

    public override ToolSpec Spec { get; } = new("memory_write",
        "Adds a note to the swarm's shared notes for every agent to read. Keep it short and factual; it is not a place for long output.",
        ToolInput.Schema(("text", "string", "The note.", true)));

    public override Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var text = ToolInput.RequiredString(input, "text").Trim().ReplaceLineEndings(" ");
        if (text.Length == 0 || text.Length > MaxChars)
            throw new ToolInputException($"A note is 1 to {MaxChars} characters.");
        Host.Store.AddNote(Host.SwarmId, Agent, text);
        Host.Changed();
        return Task.FromResult(new ToolResult("Noted."));
    }
}

public sealed class SpawnAgentTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public override ToolSpec Spec { get; } = new("spawn_agent",
        "Starts a worker (role 'coder') with its own git worktree and branch. It starts working when there are tasks it can take. " +
        "The number of agents is limited; spawn only as many as there is independent work for.",
        ToolInput.Schema(
            ("role", "string", "'coder'.", true),
            ("name", "string", "Optional name; default coder-1, coder-2, …", false),
            ("brief", "string", "Optional extra instructions for this worker, e.g. which area it owns.", false)));

    public override async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        if (!IsLead)
            return ToolResult.Error("Only the lead spawns agents.");
        var role = ToolInput.RequiredString(input, "role").Trim().ToLowerInvariant();
        var name = await Host.SpawnAsync(role, ToolInput.OptionalString(input, "name")?.Trim(), ToolInput.OptionalString(input, "brief")?.Trim(), ct).ConfigureAwait(false);
        var agent = Host.Store.Agents(Host.SwarmId).First(a => a.Name == name);
        return new ToolResult($"Started {name} (branch {agent.Branch}, worktree {agent.WorkingDirectory}).");
    }
}

public sealed class MergeAgentTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public override ToolSpec Spec { get; } = new("merge_agent",
        "Merges a worker's branch into the main working folder (lead only). It changes the user's repository, so the user is asked first. " +
        "On a conflict nothing is merged and the conflicting files are listed: create a task for a worker to resolve it.",
        ToolInput.Schema(("name", "string", "The worker's name.", true)));

    public override ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var worker = Worker(input);
        return new(RiskLevel.Exec, $"git merge {worker.Branch}", $"merge_agent({worker.Name} → {Path.GetFileName(Host.MainDirectory)})");
    }

    private SwarmAgent Worker(JsonElement input)
    {
        if (!IsLead)
            throw new ToolInputException("Only the lead merges.");
        var name = ToolInput.RequiredString(input, "name").Trim();
        return FindAgent(name) is { Branch: not null } worker
            ? worker
            : throw new ToolInputException($"There is no worker '{name}' with a branch.");
    }

    public override async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var worker = Worker(input);
        // Whatever the worker left uncommitted goes into its branch first.
        await GitWorkspace.CommitAllAsync(worker.WorkingDirectory, $"kairo: remaining changes of {worker.Name}", ct).ConfigureAwait(false);
        var result = await GitWorkspace.MergeAsync(Host.MainDirectory, worker.Branch!, $"kairo: merge {worker.Name}", ct).ConfigureAwait(false);
        if (result.Merged)
            return new ToolResult($"Merged {worker.Branch} into the main folder.\n{result.Output}".TrimEnd());
        return ToolResult.Error(result.Conflicts.Count > 0
            ? $"Conflict merging {worker.Branch}; nothing was merged. Conflicting files: {string.Join(", ", result.Conflicts)}."
            : $"Merging {worker.Branch} failed: {result.Output}");
    }
}

public sealed class SwarmFinishTool(ISwarmHost host, string agent) : SwarmTool(host, agent)
{
    public override ToolSpec Spec { get; } = new("swarm_finish",
        "Ends the swarm with a final report for the user (lead only): what was done, what was merged, what is left. Call it when the goal is met or cannot be met.",
        ToolInput.Schema(("summary", "string", "The report.", true)));

    public override Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        if (!IsLead)
            return Task.FromResult(ToolResult.Error("Only the lead finishes the swarm."));
        var summary = ToolInput.RequiredString(input, "summary").Trim();
        if (summary.Length == 0)
            throw new ToolInputException("The report is empty.");
        Host.Finish(summary);
        return Task.FromResult(new ToolResult("The swarm is finished."));
    }
}
