using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Generates the shipped icon and logo assets from the single source artwork.
//
// The source is a 1254x1254 banner: three monitors, a product wordmark and a feature strip.
// Scaled to 16px that is unreadable mush, so the icon is NOT the whole banner — it is the
// central "screen + touch ripple + cursor" mark cropped out of it. That mark is close to
// square, fills the icon box without letterboxing, and stays legible down to 16px on both
// light and dark taskbars (verified on contact sheets before this was wired into the build).
static class Program
{
    // Icon mark, as fractions of the source. Chosen by comparing candidates at real icon sizes.
    private const float MarkX = 0.408f;
    private const float MarkY = 0.455f;
    private const float MarkW = 0.190f;
    private const float MarkH = 0.190f;

    // Small frames as uncompressed DIB — the format every consumer including NSIS handles
    // without question, and only a few KB each at these sizes. Frames from 64px up as PNG:
    // a 128px DIB alone costs 67 KB where the PNG costs a fraction of that, and this icon is
    // embedded four times over (both executables, installer, uninstaller).
    private static readonly int[] IconSizes = { 16, 24, 32, 48, 64, 128, 256 };
    private const int PngFrameThreshold = 64;

    static void Main(string[] args)
    {
        var source = args[0];
        var outDir = args[1];
        Directory.CreateDirectory(outDir);

        using var src = new Bitmap(source);

        // ---- Icon mark ----
        var markRect = new Rectangle(
            (int)(MarkX * src.Width), (int)(MarkY * src.Height),
            (int)(MarkW * src.Width), (int)(MarkH * src.Height));

        using var mark = src.Clone(markRect, PixelFormat.Format32bppArgb);
        WriteIcon(mark, Path.Combine(outDir, "touchmappingagent.ico"));
        Console.WriteLine($"icon   : touchmappingagent.ico  from {markRect.Width}x{markRect.Height} crop, {IconSizes.Length} frames");

        // ---- Scaled logo ----
        // Source for the installer bitmaps below. 256px is ample for a 164x314 welcome panel and
        // costs a fraction of the 1.6 MB original.
        using (var logo = Resize(src, 256, 256))
        {
            var logoPath = Path.Combine(outDir, "logo.png");
            logo.Save(logoPath, ImageFormat.Png);
            Console.WriteLine($"logo   : logo.png  256x256  ({new FileInfo(logoPath).Length / 1024} KB)");
        }

        // ---- Installer graphics (MUI2 requires BMP, no alpha) ----
        // Flattened onto the installer's own background colour so the transparent source does
        // not come out black.
        var installerBackground = Color.FromArgb(255, 255, 255, 255);

        using (var header = MakeBmp(mark, 150, 57, installerBackground, fitHeight: true))
            SaveBmp24(header, Path.Combine(outDir, "installer-header.bmp"));
        Console.WriteLine("installer: installer-header.bmp  150x57");

        using (var welcome = MakeBmp(src, 164, 314, installerBackground, fitHeight: false))
            SaveBmp24(welcome, Path.Combine(outDir, "installer-welcome.bmp"));
        Console.WriteLine("installer: installer-welcome.bmp 164x314");
    }

    /// <summary>
    /// Writes a multi-frame .ico. Frames up to 128px are stored as 32-bit DIBs (with the
    /// mandatory AND mask); the 256px frame is stored as PNG.
    /// </summary>
    static void WriteIcon(Bitmap mark, string path)
    {
        var frames = new List<byte[]>();

        foreach (var size in IconSizes)
        {
            using var scaled = Resize(mark, size, size);
            frames.Add(size >= PngFrameThreshold ? EncodePng(scaled) : EncodeDib(scaled));
        }

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        // ICONDIR
        w.Write((ushort)0);                 // reserved
        w.Write((ushort)1);                 // type: icon
        w.Write((ushort)IconSizes.Length);

        int offset = 6 + 16 * IconSizes.Length;
        for (int i = 0; i < IconSizes.Length; i++)
        {
            int size = IconSizes[i];
            w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);               // colour count (0 = truecolour)
            w.Write((byte)0);               // reserved
            w.Write((ushort)1);             // colour planes
            w.Write((ushort)32);            // bits per pixel
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames)
            w.Write(frame);
    }

    /// <summary>
    /// Encodes a bitmap as a 32-bit bottom-up DIB with a BITMAPINFOHEADER whose height is
    /// doubled — the ICO convention that accounts for the trailing AND mask.
    /// </summary>
    static byte[] EncodeDib(Bitmap bmp)
    {
        int width = bmp.Width;
        int height = bmp.Height;
        int maskRowBytes = ((width + 31) / 32) * 4;
        int xorSize = width * height * 4;
        int andSize = maskRowBytes * height;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // BITMAPINFOHEADER
        w.Write(40);                        // biSize
        w.Write(width);                     // biWidth
        w.Write(height * 2);                // biHeight (XOR + AND)
        w.Write((ushort)1);                 // biPlanes
        w.Write((ushort)32);                // biBitCount
        w.Write(0);                         // biCompression = BI_RGB
        w.Write(xorSize + andSize);         // biSizeImage
        w.Write(0);                         // biXPelsPerMeter
        w.Write(0);                         // biYPelsPerMeter
        w.Write(0);                         // biClrUsed
        w.Write(0);                         // biClrImportant

        // XOR data: BGRA, bottom-up
        var data = bmp.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            for (int y = height - 1; y >= 0; y--)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, row, 0, row.Length);
                w.Write(row);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // AND mask: all zero. The 32-bit alpha channel carries transparency; the mask only
        // still exists because the format demands it.
        w.Write(new byte[andSize]);

        return ms.ToArray();
    }

    static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static Bitmap Resize(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }

    /// <summary>Fits the artwork into a fixed canvas, flattened onto an opaque background.</summary>
    static Bitmap MakeBmp(Bitmap source, int width, int height, Color background, bool fitHeight)
    {
        var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.Clear(background);
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        float scale = fitHeight
            ? (float)height / source.Height
            : Math.Min((float)width / source.Width, (float)height / source.Height);

        int w = Math.Max(1, (int)(source.Width * scale));
        int h = Math.Max(1, (int)(source.Height * scale));

        g.DrawImage(source, (width - w) / 2, (height - h) / 2, w, h);
        return canvas;
    }

    /// <summary>Saves as 24-bit BMP — MUI2 rejects 32-bit BMPs with an alpha channel.</summary>
    static void SaveBmp24(Bitmap source, string path)
    {
        using var flat = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(flat))
            g.DrawImage(source, 0, 0, source.Width, source.Height);

        flat.Save(path, ImageFormat.Bmp);
    }
}
