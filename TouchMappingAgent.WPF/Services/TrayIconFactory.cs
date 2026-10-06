using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Builds the notification-area icons.
///
/// Separate from <see cref="TrayIconManager"/> because this is pure image work with no UI
/// dependency — the manager needs a MainWindow and a ViewModel to construct, which would make
/// the icon logic effectively untestable if it lived there. Icon rendering is exactly the kind
/// of thing that fails silently on a target machine (missing file, wrong frame, invisible
/// badge), so it deserves its own tests.
/// </summary>
internal static class TrayIconFactory
{
    /// <summary>File name of the product icon, copied next to the executable by the build.</summary>
    internal const string IconFileName = "touchmappingagent.ico";

    /// <summary>Status colour shown when the background service answers.</summary>
    internal static readonly Color ReachableBadge = Color.FromArgb(255, 46, 176, 80);

    /// <summary>Status colour shown when the background service does not answer.</summary>
    internal static readonly Color UnreachableBadge = Color.FromArgb(255, 232, 152, 24);

    /// <summary>Full path the product icon is expected at.</summary>
    internal static string IconPath =>
        Path.Combine(AppContext.BaseDirectory, IconFileName);

    /// <summary>
    /// Loads the product icon at the size Windows wants for the notification area, so the
    /// shell picks the matching frame instead of rescaling a larger one.
    /// Falls back to the generic application icon if the file is missing or unreadable.
    /// </summary>
    internal static Icon LoadProductIcon()
    {
        try
        {
            if (File.Exists(IconPath))
            {
                return new Icon(IconPath,
                    SystemInformation.SmallIconSize.Width,
                    SystemInformation.SmallIconSize.Height);
            }
        }
        catch (Exception)
        {
            // Fall through to the system icon.
        }

        return SystemIcons.Application;
    }

    /// <summary>
    /// Draws a small status dot onto the product icon.
    ///
    /// The previous implementation swapped in SystemIcons.Information / SystemIcons.Warning,
    /// which replaced the product icon with a generic Windows glyph roughly a second after
    /// startup — so the application's own icon was never actually visible in the tray. A badge
    /// keeps the brand recognisable and still signals whether the background service answers.
    /// </summary>
    /// <param name="badgeColor">Fill colour of the status dot.</param>
    /// <returns>An icon the caller owns and must dispose.</returns>
    internal static Icon BuildBadgedIcon(Color badgeColor)
    {
        using var baseIcon = LoadProductIcon();
        using var bitmap = baseIcon.ToBitmap();

        int size = bitmap.Width;

        // A quarter of the edge, floored at 5px. A third — the first attempt — covered a
        // visible chunk of the artwork at 48 and 64px; 5px is the smallest dot that still
        // reads at 16px, which is the size that constrains the lower bound.
        int dot = Math.Max(5, size / 4);
        var dotRect = new Rectangle(size - dot - 1, size - dot - 1, dot, dot);

        using var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(bitmap, 0, 0, size, size);

            // White ring first so the dot stays visible on both light and dark taskbars.
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, Rectangle.Inflate(dotRect, 1, 1));

            using var fill = new SolidBrush(badgeColor);
            g.FillEllipse(fill, dotRect);
        }

        // Icon.FromHandle does not own the handle, so the bitmap's HICON must be destroyed
        // explicitly. Cloning first yields an independent icon that owns its own resources.
        IntPtr hIcon = canvas.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(hIcon);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
