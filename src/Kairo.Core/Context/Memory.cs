namespace Kairo.Core.Context;

/// <summary>
/// Kairo's memory: notes the user asked it to keep, in <c>&lt;user config&gt;\memory.md</c>. They go into the system prompt of every
/// session. Only the user writes here (<c>/remember</c>); the agent has no tool for it, so nothing it reads on the web or in a
/// repository can plant standing instructions.
/// </summary>
public static class Memory
{
    public const string FileName = "memory.md";
    public const int MaxNoteChars = 500;
    public const int MaxFileChars = 20_000;

    public static string PathIn(string userConfigDirectory) => System.IO.Path.Combine(userConfigDirectory, FileName);

    public static string Read(string userConfigDirectory)
    {
        var path = PathIn(userConfigDirectory);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    /// <summary>Adds one note as a list item. Returns an error text when the note cannot be kept, otherwise null.</summary>
    public static string? Append(string userConfigDirectory, string note)
    {
        note = note.ReplaceLineEndings(" ").Trim();
        if (note.Length == 0)
            return "Нечего запоминать.";
        if (note.Length > MaxNoteChars)
            return $"Заметка слишком длинная ({note.Length} символов, не больше {MaxNoteChars}). Для больших инструкций используйте AGENTS.md.";

        var existing = Read(userConfigDirectory);
        if (existing.Length + note.Length > MaxFileChars)
            return "Память заполнена. Удалите лишнее в " + PathIn(userConfigDirectory) + ".";

        Directory.CreateDirectory(userConfigDirectory);
        var prefix = existing.Length == 0 ? "# Память Kairo\n\n" : existing.EndsWith('\n') ? "" : "\n";
        File.AppendAllText(PathIn(userConfigDirectory), prefix + "- " + note + "\n");
        return null;
    }
}
