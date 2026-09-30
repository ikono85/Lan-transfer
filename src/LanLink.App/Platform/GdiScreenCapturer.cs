using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using LanLink.Core.Remote;

namespace LanLink.App.Platform;

/// <summary>
/// Capture par GDI (BitBlt) : simple et robuste. Ne capture pas les fenêtres protégées (DRM) ni le bureau
/// sécurisé (UAC, Ctrl+Alt+Suppr). Une capture DXGI Desktop Duplication serait plus rapide sur les gros écrans.
/// </summary>
public sealed class GdiScreenCapturer : IScreenCapturer
{
    private const int SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000; // CAPTUREBLT : inclut les fenêtres en couches

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, int rop);

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Point32 Position;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    private readonly Dictionary<int, (ulong Hash, int CursorX, int CursorY, bool Visible)> _last = new();
    private System.Windows.Forms.Screen[] _screens = Array.Empty<System.Windows.Forms.Screen>();
    private DateTime _screensAt = DateTime.MinValue;
    private Bitmap? _full;
    private Bitmap? _scaled;
    private byte[] _hashBuffer = Array.Empty<byte>();

    private System.Windows.Forms.Screen[] Screens()
    {
        if (DateTime.UtcNow - _screensAt > TimeSpan.FromSeconds(2) || _screens.Length == 0)
        {
            _screens = System.Windows.Forms.Screen.AllScreens;
            _screensAt = DateTime.UtcNow;
        }
        return _screens;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        _screensAt = DateTime.MinValue;
        return Screens().Select((s, i) => new MonitorInfo(i, $"Écran {i + 1}{(s.Primary ? " (principal)" : "")}",
            s.Bounds.Width, s.Bounds.Height, s.Primary)).ToList();
    }

    public ScreenRect GetBounds(int monitor)
    {
        var screens = Screens();
        var b = screens[Math.Clamp(monitor, 0, screens.Length - 1)].Bounds;
        return new ScreenRect(b.X, b.Y, b.Width, b.Height);
    }

    public CapturedFrame? Capture(int monitor, int maxWidth, int jpegQuality, bool forceFull)
    {
        var screens = Screens();
        if (monitor < 0 || monitor >= screens.Length) monitor = Array.FindIndex(screens, s => s.Primary);
        var b = screens[Math.Max(monitor, 0)].Bounds;

        var full = Reuse(ref _full, b.Width, b.Height);
        CopyScreen(full, b);

        var targetWidth = Math.Min(b.Width, Math.Max(maxWidth, 320));
        var targetHeight = Math.Max(1, (int)Math.Round((double)b.Height * targetWidth / b.Width));
        var image = full;
        if (targetWidth != b.Width)
        {
            image = Reuse(ref _scaled, targetWidth, targetHeight);
            using var g = Graphics.FromImage(image);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(full, 0, 0, targetWidth, targetHeight);
        }

        var hash = Hash(image);
        var (cursorX, cursorY, visible) = ReadCursor(b);
        var cx = (int)Math.Round(cursorX * 65535);
        var cy = (int)Math.Round(cursorY * 65535);

        var changedImage = forceFull || !_last.TryGetValue(monitor, out var last) || last.Hash != hash;
        var changedCursor = !_last.TryGetValue(monitor, out var prev) || prev.CursorX != cx || prev.CursorY != cy || prev.Visible != visible;
        _last[monitor] = (hash, cx, cy, visible);
        if (!changedImage && !changedCursor) return null;

        byte[]? jpeg = null;
        if (changedImage)
        {
            using var ms = new MemoryStream(256 * 1024);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 20, 95));
            image.Save(ms, JpegCodec, parameters);
            jpeg = ms.ToArray();
        }
        return new CapturedFrame(jpeg, targetWidth, targetHeight, cursorX, cursorY, visible);
    }

    private static void CopyScreen(Bitmap target, Rectangle bounds)
    {
        using var g = Graphics.FromImage(target);
        var destination = g.GetHdc();
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (!BitBlt(destination, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, SrcCopy | CaptureBlt))
                throw new InvalidOperationException("Capture de l'écran impossible (BitBlt).");
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
            g.ReleaseHdc(destination);
        }
    }

    private static Bitmap Reuse(ref Bitmap? bitmap, int width, int height)
    {
        if (bitmap is null || bitmap.Width != width || bitmap.Height != height)
        {
            bitmap?.Dispose();
            bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }
        return bitmap;
    }

    private ulong Hash(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var length = Math.Abs(data.Stride) * data.Height;
            if (_hashBuffer.Length < length) _hashBuffer = new byte[length];
            Marshal.Copy(data.Scan0, _hashBuffer, 0, length);
            return XxHash3.HashToUInt64(_hashBuffer.AsSpan(0, length));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static (double X, double Y, bool Visible) ReadCursor(Rectangle bounds)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info)) return (0, 0, false);
        var x = (info.Position.X - bounds.X) / (double)bounds.Width;
        var y = (info.Position.Y - bounds.Y) / (double)bounds.Height;
        var inside = x is >= 0 and <= 1 && y is >= 0 and <= 1;
        return (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), (info.Flags & 1) != 0 && inside);
    }

    public void Dispose()
    {
        _full?.Dispose();
        _scaled?.Dispose();
    }
}
