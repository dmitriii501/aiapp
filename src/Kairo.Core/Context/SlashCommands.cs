using System.Text.RegularExpressions;

namespace Kairo.Core.Context;

/// <summary>A prompt template from <c>&lt;user config&gt;\commands\NAME.md</c> or <c>&lt;project&gt;\.kairo\commands\NAME.md</c>.</summary>
public sealed record CustomCommand(string Name, string Description, string Template, string Path)
{
    public const string ArgumentsPlaceholder = "$ARGUMENTS";

    public string Expand(string arguments)
    {
        var args = arguments.Trim();
        if (Template.Contains(ArgumentsPlaceholder, StringComparison.Ordinal))
            return Template.Replace(ArgumentsPlaceholder, args, StringComparison.Ordinal);
        return args.Length == 0 ? Template : Template + "\n\nArguments: " + args;
    }
}

/// <summary>What typing <c>/something</c> means.</summary>
public abstract record SlashResult;

/// <summary>Not a command: the text goes to the model as it is.</summary>
public sealed record NotACommand : SlashResult;

/// <summary>A command Kairo runs itself (see <see cref="SlashCommands.BuiltIns"/>).</summary>
public sealed record BuiltInCommand(string Name, string Arguments) : SlashResult;

/// <summary>A custom command or a skill: the prompt goes to the model, with <see cref="Note"/> as hidden instructions.</summary>
public sealed record PromptCommand(string Typed, string Note) : SlashResult;

public sealed record UnknownCommand(string Name) : SlashResult;

/// <summary>One line of the list that opens when the user types "/". <see cref="Kind"/>: guide, built-in, command or skill.</summary>
public sealed record SlashEntry(string Name, string Description, string Kind);

public static partial class SlashCommands
{
    public static readonly IReadOnlyList<(string Name, string Description)> BuiltIns =
    [
        ("help", "список команд и скиллов"),
        ("compact", "сжать контекст сессии; можно добавить, что сохранить подробно"),
        ("remember", "записать заметку в память Kairo (она попадает во все сессии); без текста показывает память"),
        ("goal", "цель: агент не остановится, пока не выполнит её; /goal clear — снять, /goal resume — продолжить"),
    ];

    public const int MaxCommands = 100;

    public static IReadOnlyList<CustomCommand> Discover(string workingDirectory, string? userConfigDirectory)
    {
        var byName = new Dictionary<string, CustomCommand>(StringComparer.OrdinalIgnoreCase);
        if (userConfigDirectory is not null)
            Load(System.IO.Path.Combine(userConfigDirectory, "commands"), byName);
        Load(System.IO.Path.Combine(workingDirectory, ".kairo", "commands"), byName);
        return byName.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Take(MaxCommands).ToList();
    }

    private static void Load(string root, Dictionary<string, CustomCommand> into)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var file in Directory.EnumerateFiles(root, "*.md"))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            if (!NameSyntax().IsMatch(name) || BuiltIns.Any(b => b.Name == name.ToLowerInvariant()) || BuiltInGuides.Find(name) is not null)
                continue;
            try
            {
                var (front, body) = SkillCatalog.SplitFrontmatter(File.ReadAllText(file));
                var description = front.GetValueOrDefault("description") ?? body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                into[name] = new CustomCommand(name, description.Length > 120 ? description[..120] + "…" : description, body.Trim(), file);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Splits <c>/name arguments</c>. Null when the text is not shaped like a command (a path such as /etc/hosts is not one).</summary>
    public static (string Name, string Arguments)? Parse(string text)
    {
        var m = CommandSyntax().Match(text.TrimStart());
        return m.Success ? (m.Groups["name"].Value, m.Groups["args"].Value.Trim()) : null;
    }

    public static SlashResult Resolve(string text, string workingDirectory, string? userConfigDirectory)
    {
        if (Parse(text) is not { } parsed)
            return new NotACommand();

        var (name, args) = parsed;
        var lower = name.ToLowerInvariant();
        if (BuiltIns.Any(b => b.Name == lower))
            return new BuiltInCommand(lower, args);

        if (Discover(workingDirectory, userConfigDirectory).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } command)
            return new PromptCommand(text.Trim(), $"The user ran the custom command /{command.Name}. Do what it asks:\n\n{command.Expand(args)}");

        if (SkillCatalog.Discover(workingDirectory, userConfigDirectory).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } skill)
        {
            var note = $"The user invoked the skill '{skill.Name}' (folder: {skill.Directory}). Follow its instructions:\n\n{skill.Body}";
            return new PromptCommand(text.Trim(), args.Length == 0 ? note : note + "\n\nThe user's arguments: " + args);
        }

        if (BuiltInGuides.Find(name) is { } guide)
        {
            var note = $"The user chose the tool guide /{guide.Name}. Use it for this task and for the rest of the conversation:\n\n{guide.Body}";
            return new PromptCommand(text.Trim(), args.Length == 0 ? note : note + "\n\nThe user's task: " + args);
        }

        return new UnknownCommand(name);
    }

    /// <summary>Everything that can follow a "/": tool guides first, then Kairo's own commands, the user's commands and skills.</summary>
    public static IReadOnlyList<SlashEntry> List(string workingDirectory, string? userConfigDirectory)
    {
        var entries = new List<SlashEntry>();
        entries.AddRange(BuiltInGuides.All.Select(g => new SlashEntry(g.Name, g.Description, "guide")));
        entries.AddRange(BuiltIns.Select(b => new SlashEntry(b.Name, b.Description, "built-in")));
        var taken = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Discover(workingDirectory, userConfigDirectory))
            if (taken.Add(c.Name))
                entries.Add(new SlashEntry(c.Name, c.Description, "command"));
        foreach (var s in SkillCatalog.Discover(workingDirectory, userConfigDirectory))
            if (taken.Add(s.Name))
                entries.Add(new SlashEntry(s.Name, s.Description, "skill"));
        return entries;
    }

    /// <summary>The text for /help.</summary>
    public static string Help(string workingDirectory, string? userConfigDirectory)
    {
        var lines = new List<string> { "Инструменты (объясняют модели, как ими пользоваться):" };
        lines.AddRange(BuiltInGuides.All.Select(g => $"  /{g.Name} — {g.Description}"));
        lines.Add("Команды:");
        lines.AddRange(BuiltIns.Select(b => $"  /{b.Name} — {b.Description}"));
        var custom = Discover(workingDirectory, userConfigDirectory);
        if (custom.Count > 0)
        {
            lines.Add("Свои команды:");
            lines.AddRange(custom.Select(c => $"  /{c.Name}" + (c.Description.Length > 0 ? $" — {c.Description}" : "")));
        }
        var skills = SkillCatalog.Discover(workingDirectory, userConfigDirectory);
        if (skills.Count > 0)
        {
            lines.Add("Скиллы (вызываются по имени или сами):");
            lines.AddRange(skills.Select(s => $"  /{s.Name} — {s.Description}"));
        }
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"^/(?<name>[A-Za-z][A-Za-z0-9_-]{0,63})(?:\s+(?<args>[\s\S]*))?$")]
    private static partial Regex CommandSyntax();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex NameSyntax();
}
