using System.Text.Json;
using Kairo.Core.Config;
using Kairo.Core.Tests.Fakes;
using Kairo.Core.Tools;
using Kairo.PcControl;

namespace Kairo.Core.Tests;

public sealed class PcAllAppsTests
{
    private static KairoConfig Config(string pc)
    {
        var dir = Directory.CreateTempSubdirectory("kairo-all-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.toml"), "[pc]\nenabled = true\n" + pc);
        return KairoConfig.Load(dir, dir, includeProject: false);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Every_program_is_allowed_except_the_denied_ones()
    {
        var guard = new PcGuard([], allowAll: true);

        Assert.True(guard.IsAllowed("chrome.exe"));
        Assert.True(guard.IsAllowed("zen.exe"));
        Assert.False(guard.IsAllowed("powershell.exe"));
        Assert.False(guard.IsAllowed("keepassxc.exe"));
        Assert.False(guard.IsAllowed("kairo.exe"));
        Assert.Null(guard.Refusal(new WindowInfo(1, 1, "chrome.exe", "x", true, false)));
        Assert.NotNull(guard.Refusal(new WindowInfo(2, 2, "cmd.exe", "x", true, false)));
    }

    [Fact]
    public void Without_the_flag_an_empty_list_means_no_tools_and_with_it_the_tools_exist()
    {
        Assert.Empty(PcTools.For(Config(""), () => null, new FakeDesktop()));
        Assert.NotEmpty(PcTools.For(Config("all_apps = true"), () => null, new FakeDesktop()));
    }

    [Fact]
    public void A_project_cannot_turn_the_flag_on()
    {
        var user = Directory.CreateTempSubdirectory("kairo-all-user-").FullName;
        var project = Directory.CreateTempSubdirectory("kairo-all-proj-").FullName;
        File.WriteAllText(Path.Combine(user, "config.toml"), "[pc]\nenabled = true\napps = [\"notepad.exe\"]");
        Directory.CreateDirectory(Path.Combine(project, ".kairo"));
        File.WriteAllText(Path.Combine(project, ".kairo", "config.toml"), "[pc]\nall_apps = true");

        Assert.Null(KairoConfig.Load(project, user).PcAllApps);
    }

    [Fact]
    public void A_program_off_the_list_is_started_only_when_the_desktop_says_it_is_a_windowed_one()
    {
        var desktop = new FakeDesktop { LaunchRefusalText = "x is a console program" };
        var launch = PcTools.For(Config("all_apps = true"), () => null, desktop).Single(t => t.Spec.Name == "PcLaunch");
        var ctx = new ToolContext(Path.GetTempPath());

        var ex = Assert.Throws<ToolInputException>(() => launch.Assess(Json(new { application = "python.exe" }), ctx));
        Assert.Contains("console program", ex.Message);

        desktop.LaunchRefusalText = null;
        Assert.NotNull(launch.Assess(Json(new { application = "mspaint.exe" }), ctx));
    }

    [Fact]
    public void A_listed_program_is_not_inspected()
    {
        var desktop = new FakeDesktop { LaunchRefusalText = "must not be asked" };
        var launch = PcTools.For(Config("all_apps = true\napps = [\"notepad.exe\"]"), () => null, desktop).Single(t => t.Spec.Name == "PcLaunch");

        Assert.NotNull(launch.Assess(Json(new { application = "notepad.exe" }), new ToolContext(Path.GetTempPath())));
    }

    [Fact]
    public void The_real_check_refuses_console_programs_and_unknown_names_and_accepts_a_windowed_one()
    {
        Assert.Contains("console program", LaunchCheck.Refusal("cmd.exe"));
        Assert.NotNull(LaunchCheck.Refusal("definitely-not-a-program-7f3a.exe"));
        Assert.Null(LaunchCheck.Refusal("notepad.exe"));
    }
}
