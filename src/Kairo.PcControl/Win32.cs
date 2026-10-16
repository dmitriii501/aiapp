using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Kairo.PcControl;

/// <summary>The few Win32 calls the desktop needs: finding windows, bringing one to the front, typing keys and watching for the stop key.</summary>
internal static unsafe partial class Win32
{
    public const int VkPause = 0x13;

    private const uint InputKeyboard = 1;
    private const uint KeyEventExtended = 0x1;
    private const uint KeyEventKeyUp = 0x2;
    private const uint KeyEventUnicode = 0x4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const int DwmaCloaked = 14;
    private const uint ProcessQueryLimited = 0x1000;
    private const int SwRestore = 9;

    public delegate bool EnumWindowsProc(nint window, nint param);

    public sealed record Top(long Handle, int ProcessId, string Title, string ClassName, bool Minimized);

    /// <summary>Visible top-level windows. Untitled ones are included only on request (popup menus have no title).</summary>
    public static List<Top> TopLevelWindows(bool includeUntitled)
    {
        var list = new List<Top>();
        EnumWindows((hwnd, state) =>
        {
            if (!IsWindowVisible(hwnd) || IsCloaked(hwnd))
                return true;
            var title = TextOf(hwnd);
            var className = ClassOf(hwnd);
            if (title.Length == 0 && !includeUntitled)
                return true;
            if ((GetWindowLongPtr(hwnd, GwlExStyle) & WsExToolWindow) != 0 && className != "#32768")
                return true;
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            list.Add(new Top(hwnd, (int)pid, title, className, IsIconic(hwnd)));
            return true;
        }, 0);
        return list;
    }

    public static string TextOf(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return "";
        var buffer = new char[length + 1];
        fixed (char* p = buffer)
            _ = GetWindowText(hwnd, p, buffer.Length);
        return new string(buffer, 0, length);
    }

    public static string ClassOf(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var n = GetClassName(hwnd, buffer, 256);
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    private static bool IsCloaked(nint hwnd) => DwmGetWindowAttribute(hwnd, DwmaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The executable name of a process, e.g. <c>notepad.exe</c>; empty when it cannot be read.</summary>
    public static string ProcessName(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimited, false, (uint)pid);
        if (handle == 0)
            return "";
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            fixed (char* p = buffer)
            {
                if (!QueryFullProcessImageName(handle, 0, p, ref size))
                    return "";
            }
            return Path.GetFileName(new string(buffer, 0, (int)size)).ToLowerInvariant();
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>The process a window really belongs to: a Store app shows up under ApplicationFrameHost, its content under a child window.</summary>
    public static (int Pid, string Name) RealProcess(nint hwnd, int framePid)
    {
        var name = ProcessName(framePid);
        if (name != "applicationframehost.exe")
            return (framePid, name);

        var found = (Pid: framePid, Name: name);
        EnumChildWindows(hwnd, (child, state) =>
        {
            _ = GetWindowThreadProcessId(child, out var pid);
            if (pid != framePid)
            {
                found = ((int)pid, ProcessName((int)pid));
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    /// <summary>A left click at screen coordinates. Classic Win32 menus open only for a real click, not for the UI Automation patterns.</summary>
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x2, 0, 0, 0, 0); // left down
        mouse_event(0x4, 0, 0, 0, 0); // left up
    }

    /// <summary>Presses the left button at one screen point, moves to another in small steps (apps ignore a jump) and lets go.</summary>
    public static void Drag(int x1, int y1, int x2, int y2)
    {
        SetCursorPos(x1, y1);
        Thread.Sleep(30);
        mouse_event(0x2, 0, 0, 0, 0); // left down
        const int steps = 12;
        for (var i = 1; i <= steps; i++)
        {
            SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
            Thread.Sleep(12);
        }
        mouse_event(0x4, 0, 0, 0, 0); // left up
    }

    /// <summary>One stroke with the left button held: down at the first point, through the others in small steps, up at the last.</summary>
    public static void DragPath(IReadOnlyList<(int X, int Y)> points)
    {
        SetCursorPos(points[0].X, points[0].Y);
        Thread.Sleep(30);
        mouse_event(0x2, 0, 0, 0, 0); // left down
        for (var i = 1; i < points.Count; i++)
        {
            var (x1, y1) = points[i - 1];
            var (x2, y2) = points[i];
            var steps = Math.Max(1, (int)(Math.Sqrt((double)(x2 - x1) * (x2 - x1) + (double)(y2 - y1) * (y2 - y1)) / 8));
            for (var s = 1; s <= steps; s++)
            {
                SetCursorPos(x1 + (x2 - x1) * s / steps, y1 + (y2 - y1) * s / steps);
                Thread.Sleep(5);
            }
        }
        mouse_event(0x4, 0, 0, 0, 0); // left up
    }

    /// <summary>The program that owns the top-level window under a screen point, or 0.</summary>
    public static int ProcessAt(int x, int y)
    {
        var window = WindowFromPoint(new Point { X = x, Y = y });
        if (window == 0)
            return 0;
        var root = GetAncestor(window, 2 /* GA_ROOT */);
        return ProcessOf(root == 0 ? window : root);
    }

    /// <summary>A right click: the context menu of whatever is under the point.</summary>
    public static void RightClick(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x8, 0, 0, 0, 0); // right down
        mouse_event(0x10, 0, 0, 0, 0); // right up
    }

    public static void DoubleClick(int x, int y)
    {
        Click(x, y);
        Thread.Sleep(60);
        Click(x, y);
    }

    public static nint Foreground() => GetForegroundWindow();

    public static int ProcessOf(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public static bool Exists(nint hwnd) => IsWindow(hwnd);

    /// <summary>
    /// Whether the window can really be seen where it is: nine points of it are asked which program is on top there, and about three
    /// quarters must be the window's own. A picture of the screen's rectangle shows whatever covers it, so a covered window must not be
    /// photographed (what is under the cover is another program's, and it would go to a model).
    /// </summary>
    public static bool MostlyVisible(nint hwnd, int left, int top, int width, int height)
    {
        var owner = ProcessOf(hwnd);
        var real = RealProcess(hwnd, owner).Pid;
        int total = 0, own = 0;
        foreach (var fy in new[] { 0.2, 0.5, 0.8 })
        {
            foreach (var fx in new[] { 0.2, 0.5, 0.8 })
            {
                var at = ProcessAt(left + (int)(width * fx), top + (int)(height * fy));
                if (at == Environment.ProcessId)
                    continue; // Kairo's own banner is not a cover
                total++;
                if (at == owner || at == real)
                    own++;
            }
        }
        return total == 0 || own >= Math.Ceiling(total * 0.75);
    }

    public static bool BringToFront(nint hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);
        // Windows lets only the program that has the input focus hand it on. Joining the input queue of the window that has it for a moment
        // gives us that right without pressing any key (a tap of Alt would switch the menu bar of the target on).
        var foreground = GetForegroundWindow();
        var theirs = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var ours = GetCurrentThreadId();
        var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(ours, theirs, false);
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>True if Pause is down now or was pressed since this was last asked.</summary>
    public static bool StopKeyPressed() => (GetAsyncKeyState(VkPause) & 0x8001) != 0;

    // ---- Keyboard ----

    private static readonly Dictionary<string, ushort> Named = new()
    {
        ["enter"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
    };

    private static readonly HashSet<ushort> ExtendedKeys = [0x2E, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28];

    /// <summary>Presses a combination such as <c>ctrl+shift+s</c>: modifiers down, the key, modifiers up in reverse.</summary>
    public static void PressHotkey(string keys)
    {
        var parts = keys.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = new List<ushort>();
        ushort key = 0;
        foreach (var part in parts)
        {
            switch (part)
            {
                case "ctrl": modifiers.Add(0x11); break;
                case "alt": modifiers.Add(0x12); break;
                case "shift": modifiers.Add(0x10); break;
                default: key = KeyCode(part); break;
            }
        }
        if (key == 0)
            throw new ArgumentException($"'{keys}' has no key.");

        var inputs = new List<Input>();
        foreach (var m in modifiers)
            inputs.Add(Key(m, up: false));
        inputs.Add(Key(key, up: false));
        inputs.Add(Key(key, up: true));
        for (var i = modifiers.Count - 1; i >= 0; i--)
            inputs.Add(Key(modifiers[i], up: true));
        Send(inputs);
    }

    /// <summary>Types text into whatever has the keyboard focus, character by character as Unicode, so any layout works.</summary>
    public static void TypeText(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        foreach (var c in text)
        {
            inputs.Add(Unicode(c, up: false));
            inputs.Add(Unicode(c, up: true));
        }
        Send(inputs);
    }

    private static ushort KeyCode(string name)
    {
        if (Named.TryGetValue(name, out var code))
            return code;
        if (name.Length == 1 && name[0] is >= 'a' and <= 'z')
            return (ushort)char.ToUpperInvariant(name[0]);
        if (name.Length == 1 && name[0] is >= '0' and <= '9')
            return name[0];
        if (name.Length is 2 or 3 && name[0] == 'f' && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 12)
            return (ushort)(0x70 + f - 1);
        throw new ArgumentException($"Unknown key '{name}'.");
    }

    private static Input Key(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new KeyboardInput { VirtualKey = vk, Flags = (up ? KeyEventKeyUp : 0) | (ExtendedKeys.Contains(vk) ? KeyEventExtended : 0) },
    };

    private static Input Unicode(char c, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new KeyboardInput { ScanCode = c, Flags = KeyEventUnicode | (up ? KeyEventKeyUp : 0) },
    };

    private static void Send(List<Input> inputs)
    {
        var array = inputs.ToArray();
        fixed (Input* p = array)
        {
            if (SendInput((uint)array.Length, p, sizeof(Input)) != array.Length)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows did not accept the keyboard input (a window with higher rights may have the focus).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardInput Data;
        // The native INPUT union is as large as MOUSEINPUT (32 bytes on x64) and KeyboardInput is 24: 8 bytes of padding make INPUT the
        // 40 bytes SendInput insists on (it refuses any other size).
        private long _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static partial int GetWindowText(nint hwnd, char* text, int max);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static partial int GetClassName(nint hwnd, char* text, int max);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, Input* inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
