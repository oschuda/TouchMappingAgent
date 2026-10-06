using System.Collections;
using System.Reflection;
using System.Resources;
using TouchMappingAgent.WPF.Services;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Guards the branding assets.
///
/// A pack URI that no longer resolves does not fail the build — it throws when the window is
/// constructed, i.e. the first time an operator opens the UI on the target machine. Likewise,
/// the tray icon is loaded by file name at runtime: renaming or dropping it from the build
/// silently downgrades the notification area to a generic Windows glyph. Both failures are
/// invisible to every other test, so they get their own.
/// </summary>
public class BrandingAssetTests
{
    private static readonly Assembly ClientAssembly = typeof(TrayIconManager).Assembly;

    /// <summary>
    /// Reads the resource keys WPF generated for this assembly. Keys are lower-cased and use
    /// forward slashes, which is what a pack URI resolves against.
    /// </summary>
    private static HashSet<string> GetWpfResourceKeys()
    {
        var resourceName = ClientAssembly.GetName().Name + ".g.resources";
        using var stream = ClientAssembly.GetManifestResourceStream(resourceName);

        Assert.NotNull(stream);

        using var reader = new ResourceReader(stream!);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in reader)
        {
            if (entry.Key is string key)
                keys.Add(key);
        }

        return keys;
    }

    [Theory]
    [InlineData("assets/touchmappingagent.ico")]
    public void BrandingAsset_IsEmbeddedForPackUriResolution(string resourceKey)
    {
        var keys = GetWpfResourceKeys();

        Assert.True(keys.Contains(resourceKey),
            $"'{resourceKey}' is not embedded. MainWindow and LogWindow reference it via a " +
            "pack URI, which throws at window construction when the resource is missing. " +
            "Present keys: " + string.Join(", ", keys.OrderBy(k => k)));
    }

    /// <summary>
    /// The tray icon is loaded from disk by file name, not from a resource, because NotifyIcon
    /// needs a System.Drawing.Icon and the shell picks the DPI-appropriate frame from the file.
    /// It therefore has to be copied next to the executable by the build.
    /// </summary>
    [Fact]
    public void TrayIcon_IsCopiedNextToTheExecutable()
    {
        Assert.True(File.Exists(TrayIconFactory.IconPath),
            $"'{TrayIconFactory.IconPath}' is missing. TrayIconManager loads the tray icon from " +
            "this path at runtime; without it the notification area falls back to a generic " +
            "system icon.");
    }

    /// <summary>
    /// The product icon must actually load. A malformed .ico builds fine and only fails when
    /// the tray is created on the target machine.
    /// </summary>
    [Fact]
    public void TrayIcon_LoadsAtNotificationAreaSize()
    {
        using var icon = TrayIconFactory.LoadProductIcon();
        using var bitmap = icon.ToBitmap();

        Assert.True(bitmap.Width > 0 && bitmap.Height > 0);

        // The generic fallback would mean the product icon could not be read at all.
        Assert.NotEqual(System.Drawing.SystemIcons.Application.Handle, icon.Handle);
    }

    /// <summary>
    /// The badge must be visible and must not swallow the underlying artwork — the whole point
    /// of badging rather than swapping in a system glyph is that the brand stays recognisable.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrayIcon_BadgeIsDrawnWithoutHidingTheArtwork(bool reachable)
    {
        var expected = reachable ? TrayIconFactory.ReachableBadge : TrayIconFactory.UnreachableBadge;

        using var badged = TrayIconFactory.BuildBadgedIcon(expected);
        using var bitmap = badged.ToBitmap();

        // The dot sits in the bottom-right corner; sample just inside it.
        int size = bitmap.Width;
        int dot = Math.Max(5, size / 3);
        var sample = bitmap.GetPixel(size - dot / 2 - 1, size - dot / 2 - 1);

        Assert.InRange(Math.Abs(sample.R - expected.R), 0, 40);
        Assert.InRange(Math.Abs(sample.G - expected.G), 0, 40);
        Assert.InRange(Math.Abs(sample.B - expected.B), 0, 40);

        // The top-left quadrant must still be the product artwork, not badge colour.
        var artwork = bitmap.GetPixel(size / 4, size / 4);
        Assert.False(
            Math.Abs(artwork.R - expected.R) < 20 &&
            Math.Abs(artwork.G - expected.G) < 20 &&
            Math.Abs(artwork.B - expected.B) < 20,
            "The badge appears to cover the icon artwork.");
    }

    /// <summary>
    /// The icon must carry the small frames the notification area and title bar actually use.
    /// A single-resolution icon would be rescaled by the shell and look soft at 16px.
    /// </summary>
    [Fact]
    public void TrayIcon_ContainsTheSmallFramesWindowsAsksFor()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "touchmappingagent.ico");
        var bytes = File.ReadAllBytes(iconPath);

        // ICONDIR: reserved(2) type(2) count(2), then count * 16-byte ICONDIRENTRY
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));

        int count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new List<int>();

        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            int width = bytes[entry];
            sizes.Add(width == 0 ? 256 : width);   // 0 encodes 256 in the ICO format
        }

        Assert.Contains(16, sizes);   // notification area, title bar
        Assert.Contains(32, sizes);   // alt-tab, large title bar
        Assert.Contains(48, sizes);   // Explorer medium icons
        Assert.Contains(256, sizes);  // Explorer extra-large icons
    }
}
