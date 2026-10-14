using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Kairo.PcControl;

/// <summary>The Windows clipboard through the plain Win32 calls (no WinForms): text in and out, a picture in.</summary>
internal static class WinClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint handle);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);

    public static bool HasText => IsClipboardFormatAvailable(CfUnicodeText);

    public static bool HasImage => IsClipboardFormatAvailable(CfDib) || IsClipboardFormatAvailable(CfDibV5);

    /// <summary>Opens the clipboard, trying a few times: another program may hold it for a moment.</summary>
    private static bool Open()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(0))
                return true;
            Thread.Sleep(30);
        }
        return false;
    }

    public static string? ReadText()
    {
        if (!Open())
            throw new InvalidOperationException("The clipboard is in use by another program; try again.");
        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == 0)
                return null;
            var locked = GlobalLock(handle);
            if (locked == 0)
                return null;
            try
            {
                return Marshal.PtrToStringUni(locked);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static void WriteText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        SetData(CfUnicodeText, bytes);
    }

    /// <summary>Puts a picture file on the clipboard as a bitmap (white behind transparent parts), the way programs paste it.</summary>
    public static (int Width, int Height) WriteImage(string path)
    {
        using var source = new Bitmap(path);
        using var flat = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(flat))
        {
            g.Clear(Color.White);
            g.DrawImage(source, 0, 0, source.Width, source.Height);
        }
        var data = flat.LockBits(new Rectangle(0, 0, flat.Width, flat.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var rows = new byte[stride * flat.Height];
            // A DIB stores its rows bottom-up.
            for (var y = 0; y < flat.Height; y++)
                Marshal.Copy(data.Scan0 + stride * y, rows, stride * (flat.Height - 1 - y), stride);

            var header = new byte[40];
            BitConverter.GetBytes(40).CopyTo(header, 0);
            BitConverter.GetBytes(flat.Width).CopyTo(header, 4);
            BitConverter.GetBytes(flat.Height).CopyTo(header, 8);
            BitConverter.GetBytes((short)1).CopyTo(header, 12);
            BitConverter.GetBytes((short)24).CopyTo(header, 14);
            BitConverter.GetBytes(rows.Length).CopyTo(header, 20);
            SetData(CfDib, [.. header, .. rows]);
        }
        finally
        {
            flat.UnlockBits(data);
        }
        return (flat.Width, flat.Height);
    }

    private static void SetData(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
        if (memory == 0)
            throw new InvalidOperationException("Out of memory.");
        var locked = GlobalLock(memory);
        Marshal.Copy(bytes, 0, locked, bytes.Length);
        GlobalUnlock(memory);

        if (!Open())
        {
            GlobalFree(memory);
            throw new InvalidOperationException("The clipboard is in use by another program; try again.");
        }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(format, memory) == 0)
            {
                GlobalFree(memory);
                throw new InvalidOperationException("Windows refused the clipboard data.");
            }
            // The system owns the memory from here on.
        }
        finally
        {
            CloseClipboard();
        }
    }
}
