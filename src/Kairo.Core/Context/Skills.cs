using System.Text;
using System.Text.RegularExpressions;

namespace Kairo.Core.Context;

/// <summary>
/// A skill: instructions the agent loads when a task calls for them. A folder with a <c>SKILL.md</c> whose frontmatter has a
/// <c>name</c> and a <c>description</c>; the body is what the agent reads. Same layout as Claude Code skills.
/// </summary>
public sealed record Skill(string Name, string Description, string Path, string Body, bool FromProject)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;
}

public static partial class SkillCatalog
{
    public const int MaxSkills = 60;
    public const int MaxBodyChars = 40_000;
    public const int MaxDescriptionChars = 200;

    /// <summary>
    /// Skills from <c>&lt;user config&gt;\skills\*\SKILL.md</c> and <c>&lt;project&gt;\.kairo\skills\*\SKILL.md</c>.
    /// A project skill replaces a user skill of the same name.
    /// </summary>
    public static IReadOnlyList<Skill> Discover(string workingDirectory, string? userConfigDirectory)
    {
        var byName = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        if (userConfigDirectory is not null)
            Load(System.IO.Path.Combine(userConfigDirectory, "skills"), fromProject: false, byName);
        Load(System.IO.Path.Combine(workingDirectory, ".kairo", "skills"), fromProject: true, byName);
        return byName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(MaxSkills).ToList();
    }

    private static void Load(string root, bool fromProject, Dictionary<string, Skill> into)
    {
        if (!System.IO.Directory.Exists(root))
            return;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
        {
            var file = System.IO.Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file))
                continue;
            try
            {
                if (Parse(File.ReadAllText(file), System.IO.Path.GetFileName(dir), file, fromProject) is { } skill)
                    into[skill.Name] = skill;
            }
            catch (IOException)
            {
                // An unreadable skill is left out rather than failing the session.
            }
        }
    }

    public static Skill? Parse(string text, string folderName, string path, bool fromProject)
    {
        var (front, body) = SplitFrontmatter(text);
        var name = (front.GetValueOrDefault("name") ?? folderName).Trim();
        if (!ValidName().IsMatch(name))
            return null;

        var description = front.GetValueOrDefault("description")?.Trim();
        if (string.IsNullOrEmpty(description))
            description = body.Split('\n').Select(l => l.Trim().TrimStart('#', ' ')).FirstOrDefault(l => l.Length > 0) ?? "";
        description = description.ReplaceLineEndings(" ");
        if (description.Length > MaxDescriptionChars)
            description = description[..MaxDescriptionChars] + "…";

        body = body.Trim();
        if (body.Length > MaxBodyChars)
            body = body[..MaxBodyChars] + "\n[truncated]";
        return new Skill(name, description, path, body, fromProject);
    }

    /// <summary>Frontmatter is a block of <c>key: value</c> lines between two <c>---</c> lines at the top; no nesting, no lists.</summary>
    public static (Dictionary<string, string> Front, string Body) SplitFrontmatter(string text)
    {
        var front = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.ReplaceLineEndings("\n").TrimStart('﻿').Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return (front, string.Join('\n', lines));

        var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
        if (end < 0)
            return (front, string.Join('\n', lines));

        for (var i = 1; i < end; i++)
        {
            var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                continue;
            var value = lines[i][(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            front[lines[i][..colon].Trim()] = value;
        }
        return (front, string.Join('\n', lines.Skip(end + 1)));
    }

    /// <summary>The system prompt section that tells the model which skills exist.</summary>
    public static string Describe(IReadOnlyList<Skill> skills)
    {
        if (skills.Count == 0)
            return "";
        var sb = new StringBuilder();
        sb.AppendLine("<skills>");
        sb.AppendLine("Skills hold instructions for particular kinds of work. When a task matches one, load it with the Skill tool before you start.");
        foreach (var s in skills)
            sb.AppendLine($"- {s.Name}: {s.Description}");
        sb.AppendLine("</skills>");
        return sb.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex ValidName();
}
