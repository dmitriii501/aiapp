using System.Text;
using System.Text.Json;
using Kairo.Core.Models;
using Kairo.Core.Tools;

namespace Kairo.Tools;

internal static class TextFiles
{
    public const int MaxOutputChars = 30_000;

    public static bool LooksBinary(ReadOnlySpan<byte> head) => head.IndexOf((byte)0) >= 0;

    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000))))
            throw new ToolInputException($"{path} looks like a binary file.");
        return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
    }

    public static string Truncate(string s) =>
        s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + $"\n[output truncated: {s.Length - MaxOutputChars} more characters]";
}

public sealed class ReadTool : ITool
{
    private const int DefaultLimit = 2000;
    private const int MaxLineLength = 2000;

    public ToolSpec Spec { get; } = new(
        "Read",
        "Reads a text file and returns it with line numbers. Use this to look at a file before editing it. " +
        "For large files, pass offset and limit to read a part.",
        ToolInput.Schema(
            ("file_path", "string", "Absolute path, or relative to the working directory.", true),
            ("offset", "integer", "1-based line number to start from.", false),
            ("limit", "integer", "Maximum number of lines to return (default 2000).", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var path = ctx.ResolvePath(ToolInput.RequiredString(input, "file_path"));
        return new(RiskLevel.Read, path, $"Read({ctx.DisplayPath(path)})");
    }

    public Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(ToolInput.RequiredString(input, "file_path"));
        if (Directory.Exists(path))
            return Task.FromResult(ToolResult.Error($"{path} is a directory. Use Glob to list files."));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Error($"File not found: {path}"));

        var lines = TextFiles.ReadText(path).Split('\n');
        var offset = Math.Max(1, ToolInput.OptionalInt(input, "offset") ?? 1);
        var limit = Math.Max(1, ToolInput.OptionalInt(input, "limit") ?? DefaultLimit);

        var sb = new StringBuilder();
        var end = Math.Min(lines.Length, offset - 1 + limit);
        for (var i = offset - 1; i < end; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > MaxLineLength)
                line = line[..MaxLineLength] + "…";
            sb.Append((i + 1).ToString().PadLeft(6)).Append('\t').Append(line).Append('\n');
        }
        if (end < lines.Length)
            sb.Append($"[{lines.Length - end} more lines; use offset={end + 1} to continue]\n");
        if (sb.Length == 0)
            sb.Append("[empty file]");

        ctx.ReadFiles.Add(path);
        return Task.FromResult(new ToolResult(TextFiles.Truncate(sb.ToString())));
    }
}

public sealed class WriteTool : ITool
{
    public ToolSpec Spec { get; } = new(
        "Write",
        "Creates a new file or completely replaces an existing one. Prefer Edit for changes to existing files. " +
        "An existing file must be read with Read first.",
        ToolInput.Schema(
            ("file_path", "string", "Absolute path, or relative to the working directory.", true),
            ("content", "string", "The full content of the file.", true)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var path = ctx.ResolvePath(ToolInput.RequiredString(input, "file_path"));
        ToolInput.RequiredString(input, "content");
        var verb = File.Exists(path) ? "Overwrite" : "Create";
        return new(RiskLevel.Write, path, $"Write({ctx.DisplayPath(path)}) — {verb.ToLowerInvariant()}");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(ToolInput.RequiredString(input, "file_path"));
        var content = ToolInput.RequiredString(input, "content");

        if (File.Exists(path) && !ctx.ReadFiles.Contains(path))
            return ToolResult.Error($"{path} already exists and has not been read in this session. Read it first.");

        var before = File.Exists(path) ? TextFiles.ReadText(path) : "";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
        ctx.ReadFiles.Add(path);
        return new ToolResult($"Wrote {content.Split('\n').Length} lines to {ctx.DisplayPath(path)}.", Diff: UnifiedDiff.Create(before, content));
    }

    public string? Preview(JsonElement input, ToolContext ctx)
    {
        var path = ctx.ResolvePath(ToolInput.RequiredString(input, "file_path"));
        var content = ToolInput.RequiredString(input, "content");
        try
        {
            return UnifiedDiff.Create(File.Exists(path) ? TextFiles.ReadText(path) : "", content);
        }
        catch (ToolInputException)
        {
            return null; // binary file
        }
    }
}

public sealed class EditTool : ITool
{
    public ToolSpec Spec { get; } = new(
        "Edit",
        "Replaces an exact string in a file. old_string must match the file exactly (including indentation) and be " +
        "unique unless replace_all is true. Read the file first.",
        ToolInput.Schema(
            ("file_path", "string", "Absolute path, or relative to the working directory.", true),
            ("old_string", "string", "Exact text to replace.", true),
            ("new_string", "string", "Replacement text (must differ from old_string).", true),
            ("replace_all", "boolean", "Replace every occurrence (default false).", false)));

    public ToolAssessment Assess(JsonElement input, ToolContext ctx)
    {
        var path = ctx.ResolvePath(ToolInput.RequiredString(input, "file_path"));
        ToolInput.RequiredString(input, "old_string");
        ToolInput.RequiredString(input, "new_string");
        return new(RiskLevel.Write, path, $"Edit({ctx.DisplayPath(path)})");
    }

    public async Task<ToolResult> InvokeAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(ToolInput.RequiredString(input, "file_path"));
        var oldString = ToolInput.RequiredString(input, "old_string");
        var newString = ToolInput.RequiredString(input, "new_string");
        var replaceAll = ToolInput.OptionalBool(input, "replace_all");

        if (!File.Exists(path))
            return ToolResult.Error($"File not found: {path}");
        if (!ctx.ReadFiles.Contains(path))
            return ToolResult.Error($"{path} has not been read in this session. Read it first.");
        if (oldString == newString)
            return ToolResult.Error("old_string and new_string are identical.");
        if (oldString.Length == 0)
            return ToolResult.Error("old_string is empty. Use Write to create a file.");

        var text = TextFiles.ReadText(path);
        var plan = Plan(text, oldString, newString, replaceAll);
        if (plan.Error is { } error)
            return ToolResult.Error(error);

        await File.WriteAllTextAsync(path, plan.Updated!, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return new ToolResult(
            $"Edited {ctx.DisplayPath(path)} ({(replaceAll ? plan.Count : 1)} replacement{(replaceAll && plan.Count > 1 ? "s" : "")}).",
            Diff: UnifiedDiff.Create(text, plan.Updated!));
    }

    public string? Preview(JsonElement input, ToolContext ctx)
    {
        var path = ctx.ResolvePath(ToolInput.RequiredString(input, "file_path"));
        var oldString = ToolInput.RequiredString(input, "old_string");
        var newString = ToolInput.RequiredString(input, "new_string");
        if (!File.Exists(path) || oldString.Length == 0)
            return null;
        try
        {
            var text = TextFiles.ReadText(path);
            var plan = Plan(text, oldString, newString, ToolInput.OptionalBool(input, "replace_all"));
            return plan.Updated is null ? null : UnifiedDiff.Create(text, plan.Updated);
        }
        catch (ToolInputException)
        {
            return null; // binary file
        }
    }

    private sealed record EditPlan(string? Updated, int Count, string? Error);

    private static EditPlan Plan(string text, string oldString, string newString, bool replaceAll)
    {
        // Models write "\n"; keep Windows line endings intact when the file uses them.
        if (!text.Contains(oldString, StringComparison.Ordinal) && text.Contains("\r\n", StringComparison.Ordinal))
        {
            oldString = oldString.Replace("\r\n", "\n").Replace("\n", "\r\n");
            newString = newString.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        var count = CountOccurrences(text, oldString);
        if (count == 0)
            return new(null, 0, "old_string was not found in the file. Read the file again and copy the text exactly.");
        if (count > 1 && !replaceAll)
            return new(null, count, $"old_string occurs {count} times. Add surrounding context to make it unique, or set replace_all.");

        var updated = replaceAll ? text.Replace(oldString, newString, StringComparison.Ordinal) : ReplaceFirst(text, oldString, newString);
        return new(updated, count, null);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var i = text.IndexOf(oldValue, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, i), newValue, text.AsSpan(i + oldValue.Length));
    }
}
