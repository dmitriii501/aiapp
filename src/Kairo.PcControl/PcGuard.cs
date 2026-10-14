using System.Text.RegularExpressions;

namespace Kairo.PcControl;

/// <summary>
/// What may be controlled at all and which steps the fast model may not take alone (docs/specs/pc-control.md → Безопасность).
/// The allowed applications are the user's own list; the denied ones are never reachable, whatever the list says, because driving
/// them would sidestep another fence (a terminal is a shell without the shell's rules and sandbox).
/// </summary>
public sealed partial class PcGuard(IEnumerable<string> allowedApps, bool allowAll = false)
{
    private readonly HashSet<string> _allowed = new(allowedApps.Select(Normalize), StringComparer.OrdinalIgnoreCase);

    /// <summary>The user let Kairo drive every program (<c>[pc] all_apps</c>); the denied list still holds.</summary>
    public bool AllowAll { get; } = allowAll;

    /// <summary>The program is on the user's own list (as opposed to allowed only because everything is).</summary>
    public bool IsListed(string process) => _allowed.Contains(Normalize(process));

    /// <summary>Programs that cannot be controlled even when listed: terminals, password managers, system tools and Kairo itself.</summary>
    private static readonly string[] Denied =
    [
        "cmd.exe", "powershell.exe", "pwsh.exe", "windowsterminal.exe", "wt.exe", "conhost.exe", "bash.exe", "wsl.exe", "mintty.exe",
        "regedit.exe", "mmc.exe", "taskmgr.exe", "services.msc", "secpol.msc", "gpedit.msc", "msconfig.exe", "eventvwr.exe",
        "consent.exe", "credentialuibroker.exe", "logonui.exe", "lockapp.exe", "winlogon.exe", "securityhealthsystray.exe",
        "keepass.exe", "keepassxc.exe", "1password.exe", "bitwarden.exe", "lastpass.exe", "dashlane.exe", "enpass.exe", "nordpass.exe",
        "mstsc.exe", "kairo.exe", "claude.exe", "code.exe", "devenv.exe", "idea64.exe", "rider64.exe", "pycharm64.exe",
    ];

    public IReadOnlyCollection<string> Allowed => _allowed;

    public static string Normalize(string app)
    {
        var name = Path.GetFileName(app.Trim().Trim('"'));
        return (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe").ToLowerInvariant();
    }

    public bool IsDenied(string process) => Denied.Contains(Normalize(process), StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(string process) => !IsDenied(process) && (AllowAll || _allowed.Contains(Normalize(process)));

    /// <summary>The reason a window may not be touched, or null when it may.</summary>
    public string? Refusal(WindowInfo window)
    {
        if (IsDenied(window.Process))
            return $"{window.Process} cannot be controlled (terminals, password managers and system tools are always out of reach).";
        if (!AllowAll && !_allowed.Contains(Normalize(window.Process)))
            return $"{window.Process} is not in the list of applications Kairo may control. The user adds it under [pc] apps in config.toml.";
        return null;
    }

    /// <summary>
    /// Whether the fast model may do this alone. Risky steps go to the big model and the user instead, whatever the confidence:
    /// sending, paying, deleting, signing out, and anything that throws work away. Words are matched in English and Russian.
    /// </summary>
    public static string? Risk(UiElement? element, DesktopAction action)
    {
        if (action.Kind == DesktopActionKind.Hotkey)
            return HotkeyRisk(action.Text ?? "");
        if (element is null)
            return null;
        if (element.Password)
            return "a password field";
        var label = (element.Name + " " + element.AutomationId).ToLowerInvariant();
        return RiskyWords().IsMatch(label) ? $"\"{element.Name}\" looks like a step that is hard to undo" : null;
    }

    private static string? HotkeyRisk(string keys)
    {
        var k = keys.ToLowerInvariant().Replace(" ", "");
        if (k.Contains("win", StringComparison.Ordinal) || k.Contains("delete", StringComparison.Ordinal) || k is "alt+f4" or "ctrl+w" or "ctrl+shift+w" or "ctrl+q" or "ctrl+alt+delete")
            return $"the shortcut {keys} can close or remove things";
        return null;
    }

    /// <summary>A shortcut Kairo will type at all: letters, digits, F-keys and a few named keys, with ctrl/alt/shift. Not the Windows key.</summary>
    public static bool IsValidHotkey(string keys)
    {
        var k = keys.Trim().ToLowerInvariant();
        return Hotkey().IsMatch(k) && !k.Contains("win", StringComparison.Ordinal) && !(k.Contains("ctrl+", StringComparison.Ordinal) && k.Contains("alt+", StringComparison.Ordinal) && k.EndsWith("delete", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^(?:(?:ctrl|alt|shift)\+)*(?:[a-z0-9]|f(?:[1-9]|1[0-2])|enter|tab|esc|escape|space|backspace|delete|home|end|pageup|pagedown|up|down|left|right)$")]
    private static partial Regex Hotkey();

    // Matched as word starts so "Send" and "Sender" count but "Resend-able" does not matter much; Russian stems cover the usual forms.
    [GeneratedRegex(
        @"(?<![\p{L}])(delete|remove|erase|uninstall|wipe|format|reset|discard|overwrite|replace all|close|exit|quit|send|submit|publish|post|pay|purchase|buy|order|checkout|confirm|transfer|withdraw|sign out|log ?out|log off|shut ?down|restart|empty|clear all|don'?t save|"
        + @"закры|выход|удал|убрать|стер|очист|сброс|форматир|отправ|оплат|куп|заказ|оформ|подтверд|перевест|перевод|опублик|выйти|выход|завершить сеанс|выключ|перезагруз|не сохран|заменить все|отказаться)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RiskyWords();
}
