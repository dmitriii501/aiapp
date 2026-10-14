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
    public void An_action_on_an_old_look_is_refused_instead_of_hitting_the_wrong_element()
    {
        if (NoDesktop)
            return;
        var window = StartNotepad();
        var old = _desktop.Observe(window.Handle);
        var edit = old.Elements.First(e => e.Can.HasFlag(UiCapability.SetValue));
        _desktop.Observe(window.Handle); // the screen was looked at again

        var outcome = _desktop.Perform(window.Handle, new DesktopAction(DesktopActionKind.SetValue, old.Version, edit.Id, "x"));

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
