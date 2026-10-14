using System.Diagnostics;
using Kairo.Core.Tests.Fakes;
using Kairo.PcControl;
using static Kairo.Core.Tests.Fakes.ScriptedDecisionModel;

namespace Kairo.Core.Tests;

/// <summary>
/// These drive a real Notepad through UI Automation: a window appears for a few seconds. Skipped where there is no desktop session
/// (set KAIRO_NO_DESKTOP_TESTS=1 to skip them on purpose).
/// </summary>
[Collection("Desktop")]
public sealed class DesktopIntegrationTests : IDisposable
{
    private readonly UiaDesktop _desktop = new();
    private readonly List<int> _started = [];

    public void Dispose()
    {
        foreach (var pid in _started)
        {
            try
            {
                Process.GetProcessById(pid).Kill();
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    // A hosted CI runner has no desktop a user could be looking at; run these there only on purpose (KAIRO_DESKTOP_TESTS=1).
    private static bool NoDesktop =>
        Environment.GetEnvironmentVariable("KAIRO_NO_DESKTOP_TESTS") == "1"
        || !Environment.UserInteractive
        || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("KAIRO_DESKTOP_TESTS") != "1";

    private WindowInfo StartNotepad()
    {
        var window = _desktop.Launch("notepad.exe", TimeSpan.FromSeconds(10)) ?? throw new InvalidOperationException("Notepad did not show a window.");
        _started.Add(window.ProcessId);
        return window;
    }

    [Fact]
    public void A_real_window_is_read_as_a_compact_tree_quickly_and_text_can_be_set_through_the_value_pattern()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();

        var clock = Stopwatch.StartNew();
        var first = _desktop.Observe(window.Handle);
        var readMs = clock.ElapsedMilliseconds;

        Assert.Equal("notepad.exe", first.Window.Process);
        var edit = Assert.Single(first.Elements, e => e.Can.HasFlag(UiCapability.SetValue) && e.Role is "edit" or "document");
        Assert.Contains(first.Elements, e => e.Role == "menu item" && e.Can.HasFlag(UiCapability.Invoke) || e.Role == "menu item");
        Assert.True(readMs < 3000, $"reading the tree took {readMs} ms:\n{first.ToText()}");

        var done = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.SetValue, first.Version, edit.Id, "hello from Kairo"));
        Assert.True(done.Ok, done.Message);

        var second = _desktop.Observe(window.Handle);
        Assert.Contains(second.Elements, e => e.Value == "hello from Kairo");
        Assert.NotEqual(first.Digest, second.Digest);
    }

    [Fact]
    public void An_element_keeps_its_id_from_look_to_look_so_an_old_id_still_hits_the_same_element()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();
        var old = _desktop.Observe(window.Handle);
        var edit = old.Elements.First(e => e.Can.HasFlag(UiCapability.SetValue));
        var again = _desktop.Observe(window.Handle); // the screen was looked at again
        var third = _desktop.Observe(window.Handle);

        Assert.Equal(old.Elements.Select(e => e.Id), again.Elements.Select(e => e.Id));
        Assert.Equal(old.Elements.Select(e => e.Id), third.Elements.Select(e => e.Id));
        var outcome = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.SetValue, old.Version, edit.Id, "typed with an id from two looks ago"));

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains(_desktop.Observe(window.Handle).Elements, e => e.Value == "typed with an id from two looks ago");
    }

    [Fact]
    public void The_id_of_an_element_that_is_gone_is_refused_instead_of_hitting_another_one()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();
        var look = _desktop.Observe(window.Handle);

        var outcome = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.Invoke, look.Version, "e99999"));

        Assert.False(outcome.Ok);
        Assert.Contains("observe again", outcome.Message);
    }

    [Fact]
    public void A_menu_expanded_by_the_agent_shows_up_in_the_next_look_as_a_popup()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();
        var look = _desktop.Observe(window.Handle);
        // The application menu bar, not the system menu in the title bar.
        var bar = look.Elements.First(e => e.Role == "menu bar" && e.AutomationId == "MenuBar" && e.Name != "Система" && e.Name != "System");
        var file = look.Elements.SkipWhile(e => e.Id != bar.Id).First(e => e.Role == "menu item" && e.Can.HasFlag(UiCapability.Expand));

        var outcome = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.Expand, look.Version, file.Id));
        var after = _desktop.Observe(window.Handle);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains(after.Elements, e => e.Role is "menu" or "popup menu");
        Assert.True(after.Elements.Count > look.Elements.Count, after.ToText());
        // The items are listed once, though Windows shows the menu both inside the window and as a window of its own.
        Assert.Single(after.Elements, e => e.Role == "menu item" && e.AutomationId == "3");
        Assert.DoesNotContain(after.Elements, e => e.Name.Contains('\t'));
    }

    [Fact]
    public async Task The_fast_loop_types_into_a_real_window_with_a_scripted_model_and_stops_when_the_goal_shows()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();
        var loop = new PursueLoop(_desktop, new PcGuard(["notepad.exe"]), new ScriptedDecisionModel((req, n) =>
            n == 1 ? Pick(req, Starts("type:")) : Pick(req, Key("done"), achieved: 0.98)));

        var report = await loop.RunAsync(window.Handle, new PursuePlan("Write a greeting", "The editor shows the greeting", new Dictionary<string, string> { ["greeting"] = "Привет, мир" }), default);

        Assert.Equal(PursueStatus.Done, report.Status);
        Assert.Contains(report.Last!.Elements, e => e.Value == "Привет, мир");
    }

    [Fact]
    public void A_screenshot_is_taken_clicked_on_and_typed_into_without_the_tree()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();

        var shot = _desktop.Capture(window.Handle);
        var p = shot.Picture;
        Assert.True(p.Width > 100 && p.Height > 100 && p.Png.Length > 1000, $"{p.Width}x{p.Height}, {p.Png.Length} bytes");
        Assert.Equal([137, 80, 78, 71], p.Png.Take(4)); // PNG signature

        // A click in the middle of the window lands in the text area; typing goes where the focus is.
        var click = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.ClickAt, shot.Id, null, null, p.Width / 2, p.Height / 2));
        Assert.True(click.Ok, click.Message);
        var typed = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.TypeFocused, 0, null, "по пикселям"));
        Assert.True(typed.Ok, typed.Message);

        Assert.Contains(_desktop.Observe(window.Handle).Elements, e => e.Value == "по пикселям");
    }

    [Fact]
    public void A_shortcut_reaches_the_window_for_real()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();
        var look = _desktop.Observe(window.Handle);
        var edit = look.Elements.First(e => e.Can.HasFlag(UiCapability.SetValue));
        Assert.True(_desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.SetValue, look.Version, edit.Id, "old text")).Ok);
        Assert.True(_desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.Focus, _desktop.Observe(window.Handle).Version, edit.Id)).Ok);

        // Select all, then type over it: only a shortcut that really arrived leaves just the new text.
        var keys = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.Hotkey, 0, null, "ctrl+a"));
        var typed = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.TypeFocused, 0, null, "new"));

        Assert.True(keys.Ok, keys.Message);
        Assert.True(typed.Ok, typed.Message);
        Assert.Contains(_desktop.Observe(window.Handle).Elements, e => e.Value == "new");
    }

    [Fact]
    public void A_click_on_a_screenshot_that_is_not_the_latest_is_refused_and_nothing_is_clicked()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();
        var first = _desktop.Capture(window.Handle);
        _desktop.Capture(window.Handle);

        var stale = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.ClickAt, first.Id, null, null, 50, 50));
        var outside = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.ClickAt, first.Id + 1, null, null, 99999, 5));

        Assert.False(stale.Ok);
        Assert.Contains("latest screenshot", stale.Message);
        Assert.False(outside.Ok);
        Assert.Contains("outside the screenshot", outside.Message);
    }

    [Fact]
    public void The_window_list_has_notepad_with_its_process_and_title()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();

        var found = _desktop.Windows().FirstOrDefault(w => w.Handle == window.Handle);

        Assert.NotNull(found);
        Assert.Equal("notepad.exe", found.Process);
        Assert.NotEmpty(found.Title);
    }
}

/// <summary>The banner is a real window; these need a desktop session like the Notepad tests above.</summary>
[Collection("Desktop")]
public sealed class OverlayTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? title);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    // Another Kairo on this PC (the user's own app) may have a banner window of the same class: only this process's counts.
    private static nint OurBanner()
    {
        nint window = 0;
        while ((window = FindWindowEx(0, window, ControlOverlay.ClassName, null)) != 0)
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid == (uint)Environment.ProcessId)
                return window;
        }
        return 0;
    }

    private static bool NoDesktop =>
        Environment.GetEnvironmentVariable("KAIRO_NO_DESKTOP_TESTS") == "1"
        || !Environment.UserInteractive
        || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("KAIRO_DESKTOP_TESTS") != "1";

    private static bool BannerVisible() => OurBanner() is var w && w != 0 && IsWindowVisible(w);

    private static async Task<bool> Eventually(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(40);
        }
        return condition();
    }

    [Fact]
    public async Task The_banner_is_up_while_work_is_going_on_stays_for_nested_work_and_goes_when_all_of_it_ends()
    {
        if (NoDesktop)
            return;
        using var overlay = new ControlOverlay();

        var outer = overlay.Begin();
        Assert.True(await Eventually(BannerVisible), "the banner did not appear");
        var inner = overlay.Begin();
        outer.Dispose();
        outer.Dispose(); // a second dispose must not end the inner work's banner
        await Task.Delay(1000);
        Assert.True(BannerVisible(), "the banner went while nested work was still running");

        inner.Dispose();
        Assert.True(await Eventually(() => !BannerVisible()), "the banner did not go after the last work ended");
        Assert.False(overlay.IsShown);
    }

    [Fact]
    public async Task Work_that_follows_at_once_keeps_the_banner_without_flicker()
    {
        if (NoDesktop)
            return;
        using var overlay = new ControlOverlay();

        overlay.Begin().Dispose();
        await Task.Delay(250); // less than the grace period
        using var next = overlay.Begin();
        await Task.Delay(1200);

        Assert.True(BannerVisible());
    }

    [Fact]
    public async Task Disposing_the_overlay_removes_the_window()
    {
        if (NoDesktop)
            return;
        var overlay = new ControlOverlay();
        using (overlay.Begin())
            Assert.True(await Eventually(BannerVisible));

        overlay.Dispose();

        Assert.True(await Eventually(() => OurBanner() == 0));
        overlay.Begin().Dispose(); // after disposal Begin is a harmless no-op
    }
}
