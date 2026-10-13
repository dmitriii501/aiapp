using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kairo.Core.Models;
using Kairo.Core.Tools;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Kairo.Tools;

internal static class Workspace
{
    /// <summary>Directories nobody wants searched. A .gitignore-aware walker comes later.</summary>
    private static readonly string[] Excluded = ["**/.git/**", "**/node_modules/**", "**/bin/**", "**/obj/**", "**/.vs/**", "**/dist/**"];

    public static IEnumerable<string> Files(string root, string include)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(include);
        matcher.AddExcludePatterns(Excluded);
        return matcher.GetResultsInFullPath(root);
    }
}

public sealed class GlobTool : ITool
{
    private const int MaxResults = 200;

    public ToolSpec Spec { get; } = new(
        "Glob",
        "Finds files by name pattern, e.g. \"**/*.cs\" or \"src/**/Program.*\". Returns paths sorted by most recently " +
        "modified. Use this to locate files before reading them.",
        ToolInput.Schema(
            ("pattern", "string", "Glob pattern relative to path.", true),
            ("path", "string", "Directory to search (default: working directory).", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var pattern = ToolInput.RequiredString(input, "pattern");
        var root = ctx.ResolvePath(ToolInput.OptionalString(input, "path") ?? ".");
        return new(RiskLevel.Read, root, $"Glob({pattern}{(root == ctx.WorkingDirectory ? "" : " in " + ctx.DisplayPath(root))})");
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var pattern = ToolInput.RequiredString(input, "pattern");
        var root = ctx.ResolvePath(ToolInput.OptionalString(input, "path") ?? ".");
        if (!Directory.Exists(root))
            return Task.FromResult(ToolResult.Error($"Directory not found: {root}"));

        var files = Workspace.Files(root, pattern)
            .Select(f => (Path: f, Time: File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(f => f.Time)
            .Select(f => ctx.DisplayPath(Path.GetFullPath(f.Path)))
            .ToList();

        if (files.Count == 0)
            return Task.FromResult(new ToolResult("No files found."));

        var shown = files.Take(MaxResults);
        var more = files.Count > MaxResults ? $"\n[{files.Count - MaxResults} more files; narrow the pattern]" : "";
        return Task.FromResult(new ToolResult(string.Join('\n', shown) + more));
    }
}

public sealed class GrepTool : ITool
{
    private const int MaxResults = 200;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    public ToolSpec Spec { get; } = new(
        "Grep",
        "Searches file contents with a .NET regular expression. By default returns the files that match; set " +
        "output_mode to \"content\" for matching lines with line numbers. Use glob to limit which files are searched.",
        ToolInput.Schema(
            ("pattern", "string", "Regular expression.", true),
            ("path", "string", "Directory or file to search (default: working directory).", false),
            ("glob", "string", "File name filter, e.g. \"**/*.ts\" (default: all files).", false),
            ("output_mode", "string", "\"files_with_matches\" (default) or \"content\".", false),
            ("case_insensitive", "boolean", "Ignore case (default false).", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var pattern = ToolInput.RequiredString(input, "pattern");
        var root = ctx.ResolvePath(ToolInput.OptionalString(input, "path") ?? ".");
        return new(RiskLevel.Read, root, $"Grep(\"{pattern}\"{(root == ctx.WorkingDirectory ? "" : " in " + ctx.DisplayPath(root))})");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var pattern = ToolInput.RequiredString(input, "pattern");
        var root = ctx.ResolvePath(ToolInput.OptionalString(input, "path") ?? ".");
        var glob = ToolInput.OptionalString(input, "glob") ?? "**/*";
        var contentMode = ToolInput.OptionalString(input, "output_mode") == "content";

        Regex regex;
        try
        {
            var options = RegexOptions.CultureInvariant | (ToolInput.OptionalBool(input, "case_insensitive") ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error($"Invalid regular expression: {ex.Message}");
        }

        IEnumerable<string> files = File.Exists(root) ? [root]
            : Directory.Exists(root) ? Workspace.Files(root, glob)
            : [];

        var output = new List<string>();
        var matchedFiles = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (output.Count >= MaxResults)
                break;

            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes)
                continue;

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }
            if (text.Contains('\0'))
                continue;

            var display = ctx.DisplayPath(Path.GetFullPath(file));
            if (!contentMode)
            {
                if (regex.IsMatch(text))
                {
                    output.Add(display);
                    matchedFiles++;
                }
                continue;
            }

            var lines = text.Split('\n');
            var any = false;
            for (var i = 0; i < lines.Length && output.Count < MaxResults; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (!regex.IsMatch(line))
                    continue;
                output.Add($"{display}:{i + 1}: {(line.Length > 300 ? line[..300] + "…" : line)}");
                any = true;
            }
            if (any)
                matchedFiles++;
        }

        if (output.Count == 0)
            return new ToolResult("No matches.");

        var sb = new StringBuilder(string.Join('\n', output));
        if (output.Count >= MaxResults)
            sb.Append($"\n[stopped at {MaxResults} results; narrow the search]");
        return new ToolResult(TextFiles.Truncate(sb.ToString()));
    }
}
