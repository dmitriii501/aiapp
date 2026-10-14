using System.Diagnostics;
using Interop.UIAutomationClient;

namespace Kairo.PcControl;

/// <summary>
/// The real desktop, through Microsoft UI Automation (docs/specs/pc-control.md → Исполнение). The tree is read with a cache request, so a
/// window of a few hundred elements costs one cross-process call per element instead of one per property. Actions go through the
/// patterns the element offers (Invoke, Value, Toggle…), which does not depend on the resolution or on where things are drawn;
/// only hotkeys and typing into the focused element use the keyboard.
/// </summary>
public sealed class UiaDesktop : IDesktop
{
    // UIA property and pattern ids (UIAutomationClient.h); named here because the interop enums do not cover all of them.
    private const int PropName = 30005, PropControlType = 30003, PropAutomationId = 30011, PropIsEnabled = 30010, PropIsOffscreen = 30022,
        PropHasFocus = 30008, PropIsPassword = 30019, PropBounds = 30001;
    private const int PropInvoke = 30031, PropValueAvail = 30043, PropToggleAvail = 30041, PropExpandAvail = 30028, PropSelectItemAvail = 30036,
        PropScrollAvail = 30034;
    private const int PropValue = 30045, PropValueReadOnly = 30046, PropToggleState = 30086, PropExpandState = 30070;
    private const int PatternInvoke = 10000, PatternValue = 10002, PatternScroll = 10004, PatternExpand = 10005, PatternSelectItem = 10010, PatternToggle = 10015;

    private static readonly string[] Roles =
    [
        "button", "calendar", "check box", "combo box", "edit", "link", "image", "list item", "list", "menu", "menu bar", "menu item", "progress bar",
        "radio button", "scroll bar", "slider", "spinner", "status bar", "tab", "tab item", "text", "tool bar", "tooltip", "tree", "tree item", "custom",
        "group", "thumb", "data grid", "data item", "document", "split button", "window", "pane", "header", "header item", "table", "title bar", "separator",
    ];

    private static readonly Lazy<UiaDesktop> SharedDesktop = new(() => new UiaDesktop());

    /// <summary>One desktop for the whole process: the element ids of the latest look live in it, whichever tool asks next.</summary>
    public static UiaDesktop Shared => SharedDesktop.Value;

    private readonly IUIAutomation _ua = new CUIAutomation();
    private readonly object _gate = new();
    private readonly Dictionary<string, IUIAutomationElement> _elements = new();
    private readonly Dictionary<string, (int X, int Y)> _centers = new();
    private int _version;
    private int _shotId;
    private (int Id, long Handle, int Left, int Top, int Width, int Height, double Scale)? _shot;
    private readonly TimeSpan _settle;
    private readonly int _maxElements;

    /// <param name="settle">How long to let the screen react after an action before the caller looks again.</param>
    public UiaDesktop(TimeSpan? settle = null, int maxElements = 250)
    {
        _settle = settle ?? TimeSpan.FromMilliseconds(120);
        _maxElements = maxElements;
    }

    public IReadOnlyList<WindowInfo> Windows()
    {
        var foreground = Win32.Foreground();
        var list = new List<WindowInfo>();
        foreach (var top in Win32.TopLevelWindows(includeUntitled: false))
        {
            var (pid, process) = Win32.RealProcess((nint)top.Handle, top.ProcessId);
            if (process.Length == 0)
                continue;
            list.Add(new WindowInfo(top.Handle, pid, process, top.Title, top.Handle == foreground, top.Minimized));
        }
        return list;
    }

    public WindowInfo? Window(long handle)
    {
        var hwnd = (nint)handle;
        if (!Win32.Exists(hwnd))
            return null;
        var (pid, process) = Win32.RealProcess(hwnd, Win32.ProcessOf(hwnd));
        return process.Length == 0 ? null : new WindowInfo(handle, pid, process, Win32.TextOf(hwnd), Win32.Foreground() == hwnd, false);
    }

    public bool StopRequested() => Win32.StopKeyPressed();

    public WindowInfo? Launch(string application, TimeSpan wait)
    {
        var name = PcGuard.Normalize(application);
        var before = Windows().Where(w => w.Process == name).Select(w => w.Handle).ToHashSet();
        Process.Start(new ProcessStartInfo(application) { UseShellExecute = true });

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < wait)
        {
            var found = Windows().Where(w => w.Process == name).OrderByDescending(w => !before.Contains(w.Handle)).FirstOrDefault();
            if (found is not null && !before.Contains(found.Handle))
                return found;
            Thread.Sleep(100);
        }
        return Windows().FirstOrDefault(w => w.Process == name);
    }

    // ---- Reading ----

    public Observation Observe(long handle)
    {
        var info = Window(handle) ?? throw new InvalidOperationException("The window is gone.");
        var cache = NewCache();
        var walker = _ua.ControlViewWalker;
        var elements = new List<UiElement>();
        var map = new Dictionary<string, IUIAutomationElement>();
        var centers = new Dictionary<string, (int X, int Y)>();
        var clock = Stopwatch.StartNew();
        var truncated = false;

        void Walk(IUIAutomationElement parent, int depth)
        {
            if (truncated)
                return;
            IUIAutomationElement? child;
            try
            {
                child = walker.GetFirstChildElementBuildCache(parent, cache);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return;
            }

            while (child is not null)
            {
                if (elements.Count >= _maxElements || clock.ElapsedMilliseconds > 2500)
                {
                    truncated = true;
                    return;
                }

                var offscreen = Flag(child, PropIsOffscreen);
                if (!offscreen)
                {
                    var next = depth;
                    if (Describe(child, depth, elements.Count + 1) is { } element)
                    {
                        elements.Add(element);
                        map[element.Id] = child;
                        if (child.CachedBoundingRectangle is { right: > 0, bottom: > 0 } r && r.right > r.left && r.bottom > r.top)
                            centers[element.Id] = ((r.left + r.right) / 2, (r.top + r.bottom) / 2);
                        next = depth + 1;
                    }
                    if (depth < 16)
                        Walk(child, next);
                }

                try
                {
                    child = walker.GetNextSiblingElementBuildCache(child, cache);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    return;
                }
            }
        }

        // The window itself, then its owned dialogs and popup menus: a menu opened by a click is a separate window, not part of the tree.
        foreach (var hwnd in WindowsOf(info))
        {
            if (truncated)
                break;
            IUIAutomationElement root;
            try
            {
                root = _ua.ElementFromHandleBuildCache(hwnd, cache);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                continue;
            }
            var depth = 0;
            // Some menus are part of the main window's tree already; the popup window would list the same items a second time.
            if (hwnd != (nint)handle && Win32.ClassOf(hwnd) == "#32768" && elements.Any(e => e.Role == "menu"))
                continue;
            if (hwnd != (nint)handle && elements.Count < _maxElements)
            {
                var title = Win32.TextOf(hwnd);
                var id = $"e{elements.Count + 1}";
                var role = Win32.ClassOf(hwnd) == "#32768" ? "popup menu" : "dialog";
                elements.Add(new UiElement(id, role, title, null, 0, UiCapability.None, true, false, false, false, null, null, null));
                depth = 1;
            }
            Walk(root, depth);
        }

        lock (_gate)
        {
            _elements.Clear();
            _centers.Clear();
            foreach (var (id, element) in map)
                _elements[id] = element;
            foreach (var (id, center) in centers)
                _centers[id] = center;
            _version++;
            return new Observation(_version, info, elements, truncated);
        }
    }

    /// <summary>The window and every other visible window of the same process that opens on top of it (dialogs, popup menus).</summary>
    private static IEnumerable<nint> WindowsOf(WindowInfo window)
    {
        yield return (nint)window.Handle;
        foreach (var top in Win32.TopLevelWindows(includeUntitled: true))
        {
            if (top.Handle == window.Handle || top.Minimized)
                continue;
            if (Win32.RealProcess((nint)top.Handle, top.ProcessId).Pid == window.ProcessId)
                yield return (nint)top.Handle;
        }
    }

    private IUIAutomationCacheRequest NewCache()
    {
        var cache = _ua.CreateCacheRequest();
        foreach (var id in new[]
                 {
                     PropBounds, PropName, PropControlType, PropAutomationId, PropIsEnabled, PropIsOffscreen, PropHasFocus, PropIsPassword,
                     PropInvoke, PropValueAvail, PropToggleAvail, PropExpandAvail, PropSelectItemAvail, PropScrollAvail,
                     PropValue, PropValueReadOnly, PropToggleState, PropExpandState,
                 })
            cache.AddProperty(id);
        cache.TreeScope = TreeScope.TreeScope_Element;
        return cache;
    }

    /// <summary>One line for an element, or null when it adds nothing (an unnamed container with nothing to do).</summary>
    private static UiElement? Describe(IUIAutomationElement e, int depth, int number)
    {
        var typeId = (int)Prop(e, PropControlType)!;
        var role = typeId is >= 50000 and < 50000 + 39 ? Roles[typeId - 50000] : "element";
        var name = Collapse(e.CachedName ?? "", 120);
        var password = Flag(e, PropIsPassword);

        var can = UiCapability.None;
        if (Flag(e, PropInvoke)) can |= UiCapability.Invoke;
        if (Flag(e, PropValueAvail)) can |= UiCapability.SetValue;
        if (Flag(e, PropToggleAvail)) can |= UiCapability.Toggle;
        if (Flag(e, PropExpandAvail)) can |= UiCapability.Expand;
        if (Flag(e, PropSelectItemAvail)) can |= UiCapability.Select;
        if (Flag(e, PropScrollAvail)) can |= UiCapability.Scroll;
        if (Flag(e, PropHasFocus)) can |= UiCapability.Focus;

        string? value = null;
        var readOnly = false;
        if (can.HasFlag(UiCapability.SetValue))
        {
            readOnly = Flag(e, PropValueReadOnly);
            // A password is never read back: not into the model's view, not into logs.
            if (!password && Prop(e, PropValue) is string text && text.Length > 0)
                value = Collapse(text, 200);
        }

        bool? isChecked = can.HasFlag(UiCapability.Toggle) && Prop(e, PropToggleState) is int toggle ? toggle == 1 : null;
        bool? expanded = can.HasFlag(UiCapability.Expand) && Prop(e, PropExpandState) is int expand && expand != 3 ? expand is 1 or 2 : null;

        var interactive = (can & ~UiCapability.Focus) != UiCapability.None;
        if (!interactive && name.Length == 0 && value is null)
            return null; // a pane or group with nothing to say: its children are still walked
        if (role is "scroll bar" or "thumb" or "separator" or "image" && !interactive)
            return null;

        var automationId = e.CachedAutomationId;
        return new UiElement(
            $"e{number}", role, name, string.IsNullOrEmpty(automationId) ? null : automationId, depth, can,
            Flag(e, PropIsEnabled), can.HasFlag(UiCapability.Focus), password, readOnly, value, isChecked, expanded);
    }

    private static object? Prop(IUIAutomationElement e, int id)
    {
        try
        {
            var value = e.GetCachedPropertyValue(id);
            // UIA hands back its own "not supported" marker object for properties the element lacks; it is not a real value.
            return value is null || value.GetType().FullName?.Contains("ComObject", StringComparison.Ordinal) == true ? null : value;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static bool Flag(IUIAutomationElement e, int id) => Prop(e, id) is true;

    private static string Collapse(string text, int max)
    {
        var flat = text.Replace("\r\n", "\\n").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Replace('"', '\'').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    // ---- Looking at pixels ----

    public Snapshot Capture(long handle)
    {
        var hwnd = (nint)handle;
        if (!Win32.Exists(hwnd))
            throw new InvalidOperationException("The window is gone.");
        // A window behind another would be photographed with what covers it.
        if (Win32.Foreground() != hwnd)
        {
            Win32.BringToFront(hwnd);
            Thread.Sleep(150);
        }
        var bounds = ScreenCapture.WindowBounds(handle) ?? throw new InvalidOperationException("The window has no size (minimized?).");
        var picture = ScreenCapture.Region(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        lock (_gate)
        {
            _shot = (++_shotId, handle, bounds.Left, bounds.Top, bounds.Width, bounds.Height, picture.Scale);
            return new Snapshot(_shotId, picture);
        }
    }

    private ActionOutcome AtPoint(long handle, nint hwnd, DesktopAction action)
    {
        (int Id, long Handle, int Left, int Top, int Width, int Height, double Scale)? shot;
        lock (_gate)
            shot = _shot;
        if (shot is not { } s || s.Id != action.ObservationVersion || s.Handle != handle)
            return ActionOutcome.Fail("that is not the latest screenshot of this window; take a new one with PcScreenshot");
        if (action.X is not { } px || action.Y is not { } py || px < 0 || py < 0 || px > s.Width * s.Scale || py > s.Height * s.Scale)
            return ActionOutcome.Fail("the point is outside the screenshot");
        // The picture is only good while the window is where it was.
        if (ScreenCapture.WindowBounds(handle) is not { } now || now.Left != s.Left || now.Top != s.Top || now.Width != s.Width || now.Height != s.Height)
            return ActionOutcome.Fail("the window moved or changed size since the screenshot; take a new one");

        var x = s.Left + (int)Math.Round(px / s.Scale);
        var y = s.Top + (int)Math.Round(py / s.Scale);
        // Whatever is on top at that point must belong to the same program: another window may have come over it.
        var owner = Win32.ProcessAt(x, y);
        if (owner != Win32.ProcessOf(hwnd) && Win32.RealProcess(hwnd, Win32.ProcessOf(hwnd)).Pid != owner)
            return ActionOutcome.Fail("another program is on top at that point; the click was not made");

        if (action.Kind == DesktopActionKind.DoubleClickAt)
            Win32.DoubleClick(x, y);
        else
            Win32.Click(x, y);
        return ActionOutcome.Success($"clicked at ({px}, {py})");
    }

    // ---- Acting ----

    public ActionOutcome Perform(long handle, DesktopAction action)
    {
        var hwnd = (nint)handle;
        if (!Win32.Exists(hwnd))
            return ActionOutcome.Fail("the window is gone");

        try
        {
            // Menus and dialogs of many apps only react when their window is the active one; bringing it forward is part of driving it.
            if (Win32.Foreground() != hwnd)
                Win32.BringToFront(hwnd);

            ActionOutcome outcome;
            if (action.Kind == DesktopActionKind.Hotkey)
                outcome = Hotkey(hwnd, action.Text ?? "");
            else if (action.Kind is DesktopActionKind.ClickAt or DesktopActionKind.DoubleClickAt)
                outcome = AtPoint(handle, hwnd, action);
            else if (action.Kind == DesktopActionKind.TypeFocused)
                outcome = TypeFocused(hwnd, action.Text);
            else
            {
                IUIAutomationElement element;
                lock (_gate)
                {
                    if (action.ObservationVersion != _version)
                        return ActionOutcome.Fail($"the screen changed since it was read (element ids are from look {action.ObservationVersion}, the latest look is {_version}); observe again");
                    if (action.ElementId is null || !_elements.TryGetValue(action.ElementId, out element!))
                        return ActionOutcome.Fail($"there is no element {action.ElementId} in the latest look");
                }
                (int X, int Y)? center = _centers.TryGetValue(action.ElementId, out var c) ? c : null;
                outcome = OnElement(hwnd, element, action, center);
            }

            if (outcome.Ok)
                Thread.Sleep(_settle);
            return outcome;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            return ActionOutcome.Fail($"the app refused: {ex.Message.Trim()}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ActionOutcome.Fail(ex.Message);
        }
    }

    private static ActionOutcome TypeFocused(nint hwnd, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return ActionOutcome.Fail("no text to type");
        // Keys go to whatever has the focus: it must be this program, not one that came to the front meanwhile.
        if (Win32.ProcessOf(Win32.Foreground()) != Win32.ProcessOf(hwnd) && !Win32.BringToFront(hwnd))
            return ActionOutcome.Fail("the window is not in front, so the keys would go to another program");
        Win32.TypeText(text);
        return ActionOutcome.Success("typed");
    }

    private static ActionOutcome Hotkey(nint hwnd, string keys)
    {
        if (!PcGuard.IsValidHotkey(keys))
            return ActionOutcome.Fail($"'{keys}' is not a shortcut Kairo will press");
        if (!Win32.BringToFront(hwnd) && Win32.ProcessOf(Win32.Foreground()) != Win32.ProcessOf(hwnd))
            return ActionOutcome.Fail("the window could not be brought to the front, so the keys would go to another program");
        Win32.PressHotkey(keys);
        return ActionOutcome.Success($"pressed {keys}");
    }

    private ActionOutcome OnElement(nint hwnd, IUIAutomationElement element, DesktopAction action, (int X, int Y)? center)
    {
        // A classic menu item opens only for a real click; the patterns report success and nothing happens.
        if (action.Kind is DesktopActionKind.Invoke or DesktopActionKind.Expand && element.CachedControlType == 50011 && center is { } point)
        {
            Win32.Click(point.X, point.Y);
            return ActionOutcome.Success("clicked the menu item");
        }

        switch (action.Kind)
        {
            case DesktopActionKind.Invoke:
                // Invoke on a button that opens a modal dialog does not return until the dialog is closed: do not wait for it forever.
                var invoke = (IUIAutomationInvokePattern)element.GetCurrentPattern(PatternInvoke);
                var task = Task.Run(invoke.Invoke);
                if (!task.Wait(800))
                    return ActionOutcome.Success("invoked (the app is still handling it, probably a dialog)");
                task.GetAwaiter().GetResult();
                return ActionOutcome.Success("invoked");

            case DesktopActionKind.SetValue:
                if (action.Text is null)
                    return ActionOutcome.Fail("no text to set");
                if (element.CurrentIsPassword != 0)
                    return ActionOutcome.Fail("password fields are not filled by Kairo");
                ((IUIAutomationValuePattern)element.GetCurrentPattern(PatternValue)).SetValue(action.Text);
                return ActionOutcome.Success("value set");

            case DesktopActionKind.TypeText:
                if (action.Text is null)
                    return ActionOutcome.Fail("no text to type");
                if (element.CurrentIsPassword != 0)
                    return ActionOutcome.Fail("password fields are not filled by Kairo");
                element.SetFocus();
                Win32.TypeText(action.Text);
                return ActionOutcome.Success("typed");

            case DesktopActionKind.Toggle:
                ((IUIAutomationTogglePattern)element.GetCurrentPattern(PatternToggle)).Toggle();
                return ActionOutcome.Success("toggled");

            case DesktopActionKind.Expand:
                ((IUIAutomationExpandCollapsePattern)element.GetCurrentPattern(PatternExpand)).Expand();
                return ActionOutcome.Success("expanded");

            case DesktopActionKind.Collapse:
                ((IUIAutomationExpandCollapsePattern)element.GetCurrentPattern(PatternExpand)).Collapse();
                return ActionOutcome.Success("collapsed");

            case DesktopActionKind.Select:
                ((IUIAutomationSelectionItemPattern)element.GetCurrentPattern(PatternSelectItem)).Select();
                return ActionOutcome.Success("selected");

            case DesktopActionKind.ScrollDown or DesktopActionKind.ScrollUp:
                var scroll = (IUIAutomationScrollPattern)element.GetCurrentPattern(PatternScroll);
                scroll.Scroll(ScrollAmount.ScrollAmount_NoAmount,
                    action.Kind == DesktopActionKind.ScrollDown ? ScrollAmount.ScrollAmount_LargeIncrement : ScrollAmount.ScrollAmount_LargeDecrement);
                return ActionOutcome.Success("scrolled");

            case DesktopActionKind.Focus:
                element.SetFocus();
                return ActionOutcome.Success("focused");

            default:
                return ActionOutcome.Fail($"unknown action {action.Kind}");
        }
    }
}
