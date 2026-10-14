using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Kairo.PcControl;

/// <param name="Png">The picture, PNG encoded.</param>
/// <param name="Width">Pixels of the picture as sent (after any shrinking).</param>
/// <param name="Scale">Picture pixels per screen pixel; screen = picture / Scale.</param>
/// <param name="Left">Screen x of the picture's left edge (a window's position on the screen).</param>
public sealed record Screenshot(byte[] Png, int Width, int Height, double Scale, int Left, int Top);

/// <summary>Takes a picture of a window as it is on the screen, for a model that can look at images (docs/specs/pc-control.md).</summary>
public static partial class ScreenCapture
{
    /// <summary>Longest side of the picture sent to a model; larger windows are shrunk (a bigger picture costs more and reads no better).</summary>
    public const int MaxSide = 1568;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>The window's rectangle on the screen as it is now, or null when it is gone or has no size.</summary>
    public static (int Left, int Top, int Width, int Height)? WindowBounds(long handle)
    {
        if (!GetWindowRect((nint)handle, out var r) || r.Right <= r.Left || r.Bottom <= r.Top)
            return null;
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>A rectangle of the screen, shrunk so its longest side is at most <paramref name="maxSide"/>.</summary>
    public static Screenshot Region(int left, int top, int width, int height, int maxSide = MaxSide)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The area to capture has no size.");
        using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        var scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
        var (w, h) = scale < 1.0 ? ((int)Math.Round(width * scale), (int)Math.Round(height * scale)) : (width, height);
        using var picture = scale < 1.0 ? Shrink(full, w, h) : null;
        using var stream = new MemoryStream();
        (picture ?? full).Save(stream, ImageFormat.Png);
        return new Screenshot(stream.ToArray(), w, h, scale, left, top);
    }

    /// <summary>The window as shown on the screen. A window hidden behind another is captured with whatever covers it: bring it to the front first.</summary>
    public static Screenshot Window(long handle, int maxSide = MaxSide)
    {
        var b = WindowBounds(handle) ?? throw new InvalidOperationException("The window is gone or has no size.");
        return Region(b.Left, b.Top, b.Width, b.Height, maxSide);
    }

    private static Bitmap Shrink(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, width, height);
        return result;
    }
}
