namespace Kairo.PcControl;

// What the agent sees of the screen and can do to it (docs/specs/pc-control.md). Everything here is text: the window's UI Automation
// tree squeezed into a few hundred lines, and actions that name an element of that very tree. Screenshots and coordinates are not part
// of this slice: an app without a usable tree is out of reach until a vision step exists.

/// <summary>A top-level window. <see cref="Id"/> is what the model uses to name it (the handle in hex).</summary>
public sealed record WindowInfo(long Handle, int ProcessId, string Process, string Title, bool Foreground, bool Minimized)
{
    public string Id => "w" + Handle.ToString("x");
}

/// <summary>What an element can be asked to do through UI Automation patterns.</summary>
[Flags]
public enum UiCapability
{
    None = 0,
    Invoke = 1,
    SetValue = 2,
    Toggle = 4,
    Expand = 8,
    Select = 16,
    Scroll = 32,
    Focus = 64,
}

/// <param name="Id">Names the element in this observation only (<c>e1</c>, <c>e2</c>…); the next observation numbers again.</param>
/// <param name="Role">The control type in plain words: button, edit, menu item, list item…</param>
/// <param name="Value">The text of an edit or the state of a toggle, cut short. Never set for a password field.</param>
public sealed record UiElement(
    string Id,
    string Role,
    string Name,
    string? AutomationId,
    int Depth,
    UiCapability Can,
    bool Enabled,
    bool Focused,
    bool Password,
    bool ReadOnly,
    string? Value,
    bool? Checked,
    bool? Expanded);

/// <param name="Version">Counts observations of the desktop; an action must name the version it was chosen from.</param>
/// <param name="Truncated">The tree had more than the size limit; deeper or later elements are not listed.</param>
public sealed record Observation(int Version, WindowInfo Window, IReadOnlyList<UiElement> Elements, bool Truncated)
{
    /// <summary>The same screen gives the same digest; a different one means something changed.</summary>
    public string Digest => _digest ??= ComputeDigest();

    private string? _digest;

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Window ").Append(Window.Id).Append(" · ").Append(Window.Process).Append(" · \"").Append(Window.Title).Append('"').Append('\n');
        foreach (var e in Elements)
            sb.Append(Line(e)).Append('\n');
        if (Truncated)
            sb.Append("(the window has more elements than are listed)\n");
        return sb.ToString();
    }

    /// <summary>What the element looks like without its number and indentation: the same thing in two looks gives the same text.</summary>
    public static string Plain(UiElement e) => Line(e with { Id = "", Depth = 0 }).Replace("[] ", "", StringComparison.Ordinal);

    /// <summary>
    /// What changed between two looks, as the fast model needs it to judge the step it just took: elements that appeared and elements
    /// that went. Null when the two looks are the same.
    /// </summary>
    public static string? Diff(Observation before, Observation after, int max = 8)
    {
        if (before.Digest == after.Digest)
            return null;
        var counts = new Dictionary<string, int>();
        foreach (var e in before.Elements)
            counts[Plain(e)] = counts.GetValueOrDefault(Plain(e)) + 1;
        var added = new List<string>();
        foreach (var e in after.Elements)
        {
            var key = Plain(e);
            if (counts.TryGetValue(key, out var n) && n > 0)
                counts[key] = n - 1;
            else
                added.Add(key);
        }
        var removed = counts.SelectMany(p => Enumerable.Repeat(p.Key, p.Value)).ToList();

        var sb = new System.Text.StringBuilder();
        if (before.Window.Title != after.Window.Title)
            sb.Append("window title: \"").Append(after.Window.Title).Append("\"\n");
        void List(string sign, List<string> lines)
        {
            foreach (var line in lines.Take(max))
                sb.Append(sign).Append(' ').Append(line.Length > 110 ? line[..110] + "…" : line).Append('\n');
            if (lines.Count > max)
                sb.Append(sign).Append(" (and ").Append(lines.Count - max).Append(" more)\n");
        }
        List("+", added);
        List("-", removed);
        return sb.Length == 0 ? null : sb.ToString(); // only numbering or indentation moved
    }

    public static string Line(UiElement e)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(' ', e.Depth * 2).Append('[').Append(e.Id).Append("] ").Append(e.Role);
        if (e.Name.Length > 0)
            sb.Append(" \"").Append(e.Name).Append('"');
        if (e.AutomationId is { Length: > 0 })
            sb.Append(" #").Append(e.AutomationId);
        var state = new List<string>();
        if (!e.Enabled) state.Add("disabled");
        if (e.Focused) state.Add("focused");
        if (e.Password) state.Add("password");
        if (e.ReadOnly) state.Add("read-only");
        if (e.Checked is { } c) state.Add(c ? "checked" : "unchecked");
        if (e.Expanded is { } x) state.Add(x ? "expanded" : "collapsed");
        if (state.Count > 0)
            sb.Append(" (").Append(string.Join(", ", state)).Append(')');
        if (e.Value is { Length: > 0 })
            sb.Append(" = \"").Append(e.Value).Append('"');
        var can = new List<string>();
        foreach (var flag in Enum.GetValues<UiCapability>())
        {
            if (flag != UiCapability.None && flag != UiCapability.Focus && e.Can.HasFlag(flag))
                can.Add(flag.ToString().ToLowerInvariant());
        }
        if (can.Count > 0)
            sb.Append(" [").Append(string.Join(',', can)).Append(']');
        return sb.ToString();
    }

    private string ComputeDigest()
    {
        var hash = new System.IO.Hashing.XxHash3();
        hash.Append(System.Text.Encoding.UTF8.GetBytes(Window.Title));
        foreach (var e in Elements)
            hash.Append(System.Text.Encoding.UTF8.GetBytes(Line(e) + "\n"));
        return Convert.ToHexString(hash.GetCurrentHash());
    }
}

/// <param name="Id">The screenshot's number; ClickAt must name it, so a click can only land on a picture that was really taken.</param>
public sealed record Snapshot(int Id, Screenshot Picture);

public enum DesktopActionKind
{
    Invoke,
    SetValue,
    Toggle,
    Expand,
    Collapse,
    Select,
    ScrollDown,
    ScrollUp,
    Focus,
    Hotkey,
    TypeText,
    /// <summary>A click at a point of a screenshot (<c>X</c>, <c>Y</c> in the picture's pixels). For what the tree does not show; needs a vision model.</summary>
    ClickAt,
    DoubleClickAt,
    /// <summary>Types into whatever has the keyboard focus (after a ClickAt put it somewhere).</summary>
    TypeFocused,
}

/// <param name="ObservationVersion">The observation the element id comes from (for ClickAt, the screenshot number); a newer one makes the id invalid.</param>
/// <param name="ElementId">The element of that observation (null for a hotkey or a click at a point).</param>
/// <param name="Text">The text to set or type, or the key combination such as <c>ctrl+s</c>.</param>
public sealed record DesktopAction(DesktopActionKind Kind, int ObservationVersion, string? ElementId = null, string? Text = null, int? X = null, int? Y = null)
{
    public override string ToString() => Kind switch
    {
        DesktopActionKind.Hotkey => $"hotkey {Text}",
        DesktopActionKind.ClickAt or DesktopActionKind.DoubleClickAt => $"{Kind} ({X}, {Y}) in screenshot {ObservationVersion}",
        DesktopActionKind.TypeFocused => $"type into the focused element \"{Text}\"",
        DesktopActionKind.SetValue or DesktopActionKind.TypeText => $"{Kind} {ElementId} \"{Text}\"",
        _ => ElementId is null ? Kind.ToString() : $"{Kind} {ElementId}",
    };
}

public sealed record ActionOutcome(bool Ok, string Message)
{
    public static ActionOutcome Success(string message = "done") => new(true, message);

    public static ActionOutcome Fail(string message) => new(false, message);
}

/// <summary>The desktop as the agent loop uses it. The real one speaks UI Automation; tests use a scripted one.</summary>
public interface IDesktop
{
    IReadOnlyList<WindowInfo> Windows();

    WindowInfo? Window(long handle);

    /// <summary>Reads the window's tree. Each call is a new version that makes earlier element ids stale.</summary>
    Observation Observe(long handle);

    /// <summary>Does <paramref name="action"/> in the window and returns once the screen has had a moment to react.</summary>
    ActionOutcome Perform(long handle, DesktopAction action);

    /// <summary>Starts an application by its process name (it was checked against the allowed list) and returns its window when it shows up.</summary>
    WindowInfo? Launch(string application, TimeSpan wait);

    /// <summary>Why a program that is not on the user's list may not be started by name (null: it may). Only asked when every program is allowed.</summary>
    string? LaunchRefusal(string name) => null;

    /// <summary>
    /// Like <see cref="Perform(long, DesktopAction)"/> but waits only <paramref name="settle"/> afterwards: the caller looks again and
    /// waits for the screen itself, which is quicker than a pause long enough for the slowest app.
    /// </summary>
    ActionOutcome Perform(long handle, DesktopAction action, TimeSpan settle) => Perform(handle, action);

    /// <summary>A picture of the window as it is on the screen (it is brought to the front first). The returned number names it for ClickAt.</summary>
    Snapshot Capture(long handle);

    /// <summary>True when the user pressed the stop key (Pause) since the last time this was asked.</summary>
    bool StopRequested();
}
