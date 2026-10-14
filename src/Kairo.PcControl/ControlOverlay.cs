using System.Runtime.InteropServices;

namespace Kairo.PcControl;

/// <summary>
/// The on-screen sign that Kairo is driving the PC: a red banner at the top of the screen, above every window, that takes no clicks and
/// no focus. It shows while any PC tool is running (several in a row keep it up) and goes a moment after the last one ends. It does not
/// depend on the Kairo window, so it is there when the agent works in another program (docs/specs/pc-control.md → Безопасность).
/// </summary>
public sealed unsafe partial class ControlOverlay : IDisposable
{
    public const string ClassName = "KairoPcControlBanner";
    public const string Text = "Kairo управляет ПК · Pause — стоп";

    private const int WmApp = 0x8000, WmShow = WmApp + 1, WmHide = WmApp + 2, WmPaint = 0xF, WmDestroy = 0x2, WmClose = 0x10;
    private const uint WsPopup = 0x80000000;
    private const uint ExTopmost = 0x8, ExTransparent = 0x20, ExToolWindow = 0x80, ExLayered = 0x80000, ExNoActivate = 0x08000000;
    private const int SwHide = 0, SwShowNoActivate = 4;
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(600);

    private static readonly Lazy<ControlOverlay> SharedOverlay = new(() => new ControlOverlay());

    /// <summary>One banner for the process.</summary>
    public static ControlOverlay Shared => SharedOverlay.Value;

    private readonly object _gate = new();
    private int _active;
    private nint _window;
    private Thread? _thread;
    private Timer? _hideTimer;
    private bool _disposed;
    // One delegate for the whole process, never replaced: the window class keeps a pointer to it for as long as the process lives.
    private static readonly WndProc Proc = WindowProc;
    private static readonly object ClassGate = new();
    private static bool _classRegistered;

    /// <summary>True while the banner is on screen (for tests and diagnostics).</summary>
    public bool IsShown { get; private set; }

    /// <summary>Marks the start of work; the banner stays up until every returned handle is disposed.</summary>
    public IDisposable Begin()
    {
        lock (_gate)
        {
            if (_disposed)
                return new Handle(null);
            _hideTimer?.Dispose();
            _hideTimer = null;
            _active++;
            EnsureWindow();
            if (_window != 0)
            {
                PostMessage(_window, WmShow, 0, 0);
                IsShown = true;
            }
        }
        return new Handle(this);
    }

    private void End()
    {
        lock (_gate)
        {
            if (_active > 0)
                _active--;
            if (_active == 0 && !_disposed)
            {
                // A short grace period: tools often follow one another, and a banner that flickers between them says little.
                _hideTimer?.Dispose();
                _hideTimer = new Timer(_ =>
                {
                    lock (_gate)
                    {
                        if (_active == 0 && _window != 0)
                        {
                            PostMessage(_window, WmHide, 0, 0);
                            IsShown = false;
                        }
                    }
                }, null, HideDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void EnsureWindow()
    {
        if (_window != 0 || _thread is not null)
            return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Pump(ready)) { IsBackground = true, Name = "Kairo PC banner" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
    }

    private void Pump(ManualResetEventSlim ready)
    {
        var module = GetModuleHandle(null);
        lock (ClassGate)
        {
            if (!_classRegistered)
            {
                fixed (char* className = ClassName)
                {
                    var wc = new WindowClass
                    {
                        cbSize = (uint)sizeof(WindowClass),
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                        hInstance = module,
                        lpszClassName = className,
                        hbrBackground = CreateSolidBrush(0x000014E5), // #E51400 as BGR
                    };
                    _classRegistered = RegisterClassEx(&wc) != 0;
                }
            }
        }

        var width = 360;
        var height = 28;
        var x = Math.Max(0, (GetSystemMetrics(0) - width) / 2);
        var window = CreateWindowEx(ExTopmost | ExTransparent | ExToolWindow | ExLayered | ExNoActivate, ClassName, "Kairo", WsPopup, x, 0, width, height, 0, 0, module, 0);
        if (window != 0)
        {
            SetLayeredWindowAttributes(window, 0, 235, 0x2);
            _window = window;
        }
        ready.Set();
        if (window == 0)
            return;

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessage(&msg);
        }
    }

    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmShow:
                SetWindowPos(window, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40); // no size, no move, no activate, show
                ShowWindow(window, SwShowNoActivate);
                return 0;
            case WmHide:
                ShowWindow(window, SwHide);
                return 0;
            case WmPaint:
                Paint(window);
                return 0;
            case WmClose:
                DestroyWindow(window);
                return 0;
            case WmDestroy:
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private static void Paint(nint window)
    {
        var paint = new PaintStruct();
        var dc = BeginPaint(window, &paint);
        GetClientRect(window, out var rect);
        var font = CreateFont(-16, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        var old = SelectObject(dc, font);
        SetBkMode(dc, 1);
        SetTextColor(dc, 0x00FFFFFF);
        fixed (char* text = Text)
            DrawText(dc, text, -1, ref rect, 0x1 | 0x4 | 0x20); // centred, vertically centred, one line
        SelectObject(dc, old);
        DeleteObject(font);
        EndPaint(window, &paint);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _hideTimer?.Dispose();
            if (_window != 0)
                PostMessage(_window, WmClose, 0, 0);
            IsShown = false;
        }
    }

    private sealed class Handle(ControlOverlay? owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                owner?.End();
        }
    }

    // ---- Win32 ----

    private delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint Dc;
        public int Erase;
        public Rect Paint;
        public int Restore, IncUpdate;
        public fixed byte Reserved[32];
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static partial ushort RegisterClassEx(WindowClass* wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Message message, nint window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Message* message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(Message* message);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint BeginPaint(nint window, PaintStruct* paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndPaint(nint window, PaintStruct* paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW")]
    private static partial int DrawText(nint dc, char* text, int length, ref Rect rect, uint format);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);
}
