using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.Swarm;

/// <summary>
/// Read-only git for a worker whose shell is sandboxed. <c>git</c> cannot start inside the sandbox (Windows denies it the NUL device),
/// so this runs it from the trusted side, and is built so that it can only look: the git directory is the one git made for the worktree
/// (never the worktree's own <c>.git</c> file, which the worker can rewrite), config that runs commands is switched off, and the
/// subcommand and every option must be on a list. What it cannot do — change anything, read a file outside the repository (<c>--no-index</c>,
/// <c>blame --contents</c>), run an external diff or textconv — is not on the list.
/// </summary>
public sealed partial class GitReadTool(string repository, string worktree) : ITool
{
    public const int TimeoutMs = 30_000;

    public ToolSpec Spec { get; } = new(
        "Git",
        "Read-only git for your worktree: status, diff, log, show, blame, ls-files, branch, rev-parse. Use it where the shell's git does not start " +
        "(the sandbox). It cannot change anything; commits are made for you. Give the command as you would type it after 'git', for example " +
        "'status --short', 'diff --stat', 'log --oneline -n 10' or 'show HEAD:README.md'.",
        ToolInput.Schema(("command", "string", "The git command without the word git, e.g. 'diff --stat'.", true)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var args = Validate(ToolInput.RequiredString(input, "command"));
        return new ToolAssessment(RiskLevel.Read, worktree, $"git {string.Join(' ', args)}");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var args = Validate(ToolInput.RequiredString(input, "command"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeoutMs);
        try
        {
            // Diffs never go through external programs or text converters named in some config.
            var extra = args[0] is "diff" or "log" or "show" ? new[] { "--no-ext-diff", "--no-textconv", "--no-color" } : [];
            var (code, output) = await GitWorkspace.RunInWorktreeAsync(repository, worktree, [args[0], .. extra, .. args.Skip(1)], timeout.Token).ConfigureAwait(false);
            var text = output.Length == 0 ? "(no output)" : output;
            return code == 0 ? new ToolResult(Clip(text)) : ToolResult.Error(Clip($"git exited with {code}\n{text}"));
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error($"Git is not available for this worktree: {ex.Message}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Error($"git took longer than {TimeoutMs / 1000} s and was stopped.");
        }
    }

    private const int MaxChars = 30_000;

    private static string Clip(string text) =>
        text.Length <= MaxChars ? text : text[..MaxChars] + $"\n[output cut at {MaxChars} characters; ask for less: a path, -n, --stat]";

    // ---- What may be asked ----

    private static readonly string[] Subcommands = ["status", "diff", "log", "show", "blame", "ls-files", "branch", "rev-parse"];

    private static readonly string[] Display =
    [
        "--stat", "--numstat", "--shortstat", "--name-only", "--name-status", "--oneline", "--abbrev-commit", "--no-merges", "--merges", "--first-parent",
        "--reverse", "--graph", "-n", "-p", "--patch", "-s", "--no-patch", "-w", "--ignore-all-space", "-b", "--ignore-space-change", "--summary", "--decorate",
        "--no-decorate", "--full-index", "--check", "--raw", "--all", "--follow", "--color=never",
    ];

    private static readonly Dictionary<string, string[]> Exact = new()
    {
        ["status"] = ["-s", "--short", "-b", "--branch", "--porcelain", "--porcelain=v1", "--ignored", "-u", "-uall", "-unormal", "-uno"],
        ["diff"] = [.. Display, "--cached", "--staged"],
        ["log"] = [.. Display],
        ["show"] = [.. Display],
        ["blame"] = ["-w", "-l"],
        ["ls-files"] = ["-s", "--stage", "-m", "--modified", "-o", "--others", "-d", "--deleted", "-c", "--cached", "--exclude-standard"],
        ["branch"] = ["-a", "--all", "-r", "--remotes", "-v", "-vv", "--list", "--show-current"],
        ["rev-parse"] = ["--abbrev-ref", "--short", "--verify", "--is-inside-work-tree"],
    };

    // Values are patterns or numbers: nothing here names a file or a program.
    private static readonly Dictionary<string, Regex[]> Patterns = new()
    {
        ["status"] = [UntrackedFiles()],
        ["diff"] = [Number(), Context(), DiffFilter(), Since()],
        ["log"] = [Number(), Context(), DiffFilter(), Since(), Pattern(), Format(), DateFormat()],
        ["show"] = [Number(), Context(), DiffFilter(), Pattern(), Format(), DateFormat()],
        ["blame"] = [Lines()],
    };

    /// <summary>Checks the command and returns its arguments; throws <see cref="ToolInputException"/> with the reason when anything is off the list.</summary>
    internal static string[] Validate(string command)
    {
        if (command.Length > 600 || command.Any(c => c is '\0' or '\n' or '\r'))
            throw new ToolInputException("The command is too long or has line breaks.");
        var tokens = Tokenize(command);
        if (tokens.Count == 0)
            throw new ToolInputException($"Give a git command. Allowed: {string.Join(", ", Subcommands)}.");
        if (tokens[0] == "git")
            tokens.RemoveAt(0);
        var sub = tokens[0];
        if (!Subcommands.Contains(sub))
            throw new ToolInputException($"'{sub}' is not available here. Allowed (read-only): {string.Join(", ", Subcommands)}.");

        var afterDashes = false;
        foreach (var token in tokens.Skip(1))
        {
            if (!afterDashes && token == "--")
            {
                afterDashes = true;
                continue;
            }
            if (!afterDashes && token.StartsWith('-'))
            {
                var ok = Exact[sub].Contains(token) || Patterns.TryGetValue(sub, out var patterns) && patterns.Any(p => p.IsMatch(token));
                if (!ok)
                    throw new ToolInputException($"The option '{token}' is not allowed for git {sub}. This tool only reads, and takes a fixed set of options.");
                continue;
            }
            CheckPositional(sub, token);
        }
        return [.. tokens];
    }

    private static void CheckPositional(string sub, string token)
    {
        if (token.Length == 0 || token.Length > 300)
            throw new ToolInputException("An empty or very long argument.");
        // "rev:path" reads a file as it is in a commit; the path part must stay inside the repository like any other path.
        if (sub == "show" && token.Contains(':'))
        {
            var at = token.IndexOf(':');
            var (rev, path) = (token[..at], token[(at + 1)..]);
            if (!RevisionChars().IsMatch(rev))
                throw new ToolInputException($"'{rev}' is not a revision.");
            CheckPath(path);
            return;
        }
        if (token.StartsWith(':'))
            throw new ToolInputException("Pathspec magic (':…') is not allowed.");
        CheckPath(token);
    }

    private static void CheckPath(string path)
    {
        if (path.StartsWith('/') || path.StartsWith('\\') || (path.Length > 1 && path[1] == ':'))
            throw new ToolInputException($"'{path}' is outside the repository: give paths relative to your worktree.");
        if (path.Split('/', '\\').Any(part => part == ".."))
            throw new ToolInputException($"'{path}' goes above your worktree.");
    }

    /// <summary>Splits on spaces; double quotes keep a value together (--author="Ann Lee").</summary>
    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var c in command)
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (c == ' ' && !quoted)
            {
                if (started)
                    tokens.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (quoted)
            throw new ToolInputException("A double quote is not closed.");
        if (started)
            tokens.Add(current.ToString());
        return tokens;
    }

    [GeneratedRegex(@"^(-n\d+|-\d+|--max-count=\d+)$")]
    private static partial Regex Number();

    [GeneratedRegex(@"^(-U\d+|--unified=\d+)$")]
    private static partial Regex Context();

    [GeneratedRegex(@"^--diff-filter=[ACDMRTUXB]+$")]
    private static partial Regex DiffFilter();

    [GeneratedRegex(@"^--(since|until|after|before)=[^\s].*$")]
    private static partial Regex Since();

    [GeneratedRegex(@"^--(author|committer|grep)=.+$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"^--(format|pretty)=(oneline|short|medium|full|fuller|reference|format:.+|tformat:.+|%.+)$")]
    private static partial Regex Format();

    [GeneratedRegex(@"^--date=(iso|iso-strict|short|relative|local|default|rfc|human|unix)$")]
    private static partial Regex DateFormat();

    [GeneratedRegex(@"^-L\d+(,\d+)?$")]
    private static partial Regex Lines();

    [GeneratedRegex(@"^--untracked-files=(all|normal|no)$")]
    private static partial Regex UntrackedFiles();

    [GeneratedRegex(@"^[A-Za-z0-9_./^~@{}\-]+$")]
    private static partial Regex RevisionChars();
}
