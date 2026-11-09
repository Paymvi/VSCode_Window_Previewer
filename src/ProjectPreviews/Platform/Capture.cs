using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProjectPreviews.Platform;

/// <summary>
/// One-shot window snapshots for the card backgrounds. PrintWindow with PW_RENDERFULLCONTENT asks DWM for the
/// window's own content (works for occluded GPU-rendered windows like VS Code). Images live only in memory,
/// downscaled to <see cref="MaxWidth"/>, and are taken only when the picker opens.
/// </summary>
public static class Capture
{
    public const int MaxWidth = 560;

    /// <summary>Null when the window is minimized, gone, or the capture came back blank (a black card would be misleading).</summary>
    public static BitmapSource? Snapshot(nint hwnd)
    {
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || !Native.GetWindowRect(hwnd, out var r) || r.Width < 50 || r.Height < 50)
            return null;
        int w = r.Width, h = r.Height;
        int tw = Math.Min(MaxWidth, w), th = Math.Max(1, (int)Math.Round(h * (tw / (double)w)));

        nint screen = Native.GetDC(0);
        nint fullDc = Native.CreateCompatibleDC(screen), smallDc = Native.CreateCompatibleDC(screen);
        nint fullBmp = Dib(screen, w, h, out _), smallBmp = Dib(screen, tw, th, out nint bits);
        try
        {
            if (fullBmp == 0 || smallBmp == 0) return null;
            nint oldFull = Native.SelectObject(fullDc, fullBmp), oldSmall = Native.SelectObject(smallDc, smallBmp);
            bool ok = Native.PrintWindow(hwnd, fullDc, Native.PW_RENDERFULLCONTENT);
            if (ok)
            {
                Native.SetStretchBltMode(smallDc, Native.HALFTONE);
                ok = Native.StretchBlt(smallDc, 0, 0, tw, th, fullDc, 0, 0, w, h, Native.SRCCOPY);
            }
            Native.SelectObject(fullDc, oldFull);
            Native.SelectObject(smallDc, oldSmall);
            if (!ok) return null;

            int stride = tw * 4;
            var pixels = new byte[stride * th];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            if (LooksBlank(pixels)) return null;
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI leaves alpha undefined
            var bmp = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            if (fullBmp != 0) Native.DeleteObject(fullBmp);
            if (smallBmp != 0) Native.DeleteObject(smallBmp);
            Native.DeleteDC(fullDc);
            Native.DeleteDC(smallDc);
            Native.ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// Gaussian-like blur (three box passes) baked into the image once, off the UI thread. The picker renders in
    /// software to keep memory low, where a live BlurEffect on every card would make the first frame too slow.
    /// </summary>
    public static BitmapSource Blur(BitmapSource src, double radiusPx)
    {
        if (radiusPx < 0.5) return src;
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var a = new byte[stride * h];
        src.CopyPixels(a, stride, 0);
        var b = new byte[a.Length];
        foreach (int r in BoxRadii(radiusPx / 2, 3))
        {
            BoxPass(a, b, w, h, r, horizontal: true);
            BoxPass(b, a, w, h, r, horizontal: false);
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, a, stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Box radii whose three passes approximate a Gaussian of <paramref name="sigma"/> (Kovesi's method).</summary>
    internal static int[] BoxRadii(double sigma, int n)
    {
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        int m = (int)Math.Round((12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4));
        return Enumerable.Range(0, n).Select(i => ((i < m ? wl : wu) - 1) / 2).ToArray();
    }

    /// <summary>Running-sum box blur along one axis, edges clamped (so no dark fringe).</summary>
    private static void BoxPass(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        if (r <= 0) { Buffer.BlockCopy(src, 0, dst, 0, src.Length); return; }
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        int step = horizontal ? 4 : w * 4;
        double inv = 1.0 / (2 * r + 1);
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * w * 4 : line * 4;
            for (int c = 0; c < 4; c++)
            {
                int Px(int i) => src[start + Math.Clamp(i, 0, len - 1) * step + c];
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += Px(i);
                for (int i = 0; i < len; i++)
                {
                    dst[start + i * step + c] = (byte)(sum * inv + 0.5);
                    sum += Px(i + r + 1) - Px(i - r);
                }
            }
        }
    }

    private static nint Dib(nint dc, int w, int h, out nint bits)
    {
        var bi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        return Native.CreateDIBSection(dc, ref bi, 0, out bits, 0, 0);
    }

    /// <summary>All (nearly) one color: PrintWindow failed silently.</summary>
    internal static bool LooksBlank(byte[] bgra)
    {
        if (bgra.Length < 4) return true;
        int step = Math.Max(4, bgra.Length / 4 / 2000 * 4);
        byte b0 = bgra[0], g0 = bgra[1], r0 = bgra[2];
        for (int i = 0; i + 2 < bgra.Length; i += step)
            if (Math.Abs(bgra[i] - b0) > 12 || Math.Abs(bgra[i + 1] - g0) > 12 || Math.Abs(bgra[i + 2] - r0) > 12)
                return false;
        return true;
    }
}
