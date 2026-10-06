using Evolved.EdidManager.Edid;
using Xunit;

namespace TouchMappingAgent.Tests.EdidTests;

/// <summary>
/// Tests for the emergency EDID synthesizer.
///
/// This is the last-resort path — no DDC, no vendor INF, no .bin. The block it produces is not
/// the panel's real EDID and never can be, so the bar is: standards-conforming enough that the
/// driver accepts it and the port stays alive. Every test therefore ends at the checksum and
/// the round-trip through the parser, because a block the driver rejects leaves the operator
/// with a dark screen and no fallback at all.
/// </summary>
public class EdidSynthesizerTests
{
    [Theory]
    [InlineData(1920, 1080, 60)]
    [InlineData(1920, 1080, 59)]
    [InlineData(1280, 1024, 60)]
    [InlineData(1024, 768, 60)]
    [InlineData(2560, 1080, 60)]
    [InlineData(800, 600, 75)]
    [InlineData(3840, 2160, 30)]
    public void Synthesize_ProducesAValidBlockForEveryMode(int width, int height, int refresh)
    {
        var bytes = EdidSynthesizer.SynthesizeFromDisplayMode(width, height, refresh, "TestPanel");

        Assert.Equal(EdidBlock.BlockSize, bytes.Length);

        var edid = EdidBlock.FromBytes(bytes);
        Assert.True(edid.IsChecksumValid, $"{width}x{height}@{refresh} produced an invalid checksum.");
    }

    [Fact]
    public void Synthesize_IsIdentifiableAsSynthetic()
    {
        var edid = EdidBlock.FromBytes(
            EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000"));

        // Stamped with a distinct manufacturer and product code so a survey can tell a
        // synthesised block from a real panel's EDID at a glance.
        Assert.Equal(EdidSynthesizer.SyntheticManufacturer, edid.ManufacturerCode);
        Assert.Equal(EdidSynthesizer.SyntheticProductCode, edid.ProductCode);
    }

    [Fact]
    public void Synthesize_CarriesTheRequestedMonitorName()
    {
        var edid = EdidBlock.FromBytes(
            EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000"));

        Assert.Equal("DM7000", edid.MonitorName);
    }

    [Fact]
    public void Synthesize_TruncatesAnOverlongMonitorName()
    {
        var edid = EdidBlock.FromBytes(
            EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, new string('N', 40)));

        Assert.Equal(new string('N', EdidBlock.DescriptorTextLength), edid.MonitorName);
        Assert.True(edid.IsChecksumValid);
    }

    [Fact]
    public void Synthesize_WritesTheSerialNumberAndText()
    {
        var edid = EdidBlock.FromBytes(EdidSynthesizer.SynthesizeFromDisplayMode(
            1920, 1080, 60, "DM7000", serialNumber: 1001, serialText: "880-A"));

        Assert.Equal(1001u, edid.SerialNumber);
        Assert.Equal("880-A", edid.SerialText);
        Assert.True(edid.IsChecksumValid);
    }

    /// <summary>
    /// With no serial text the last descriptor must be the standard 0x10 "unused" filler, not
    /// zeros — and it must then be reusable, so a later ApplyCustomTemplate can stamp a serial
    /// into it.
    /// </summary>
    [Fact]
    public void Synthesize_LeavesAReusableDescriptorWhenNoSerialTextIsGiven()
    {
        var bytes = EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000");
        var edid = EdidBlock.FromBytes(bytes);

        Assert.Null(edid.SerialText);
        Assert.True(EdidMutator.FindReusableDescriptorOffset(bytes) >= 0);
    }

    /// <summary>
    /// The synthesised block must survive the same mutation path a real EDID goes through,
    /// checksum included — otherwise two extender ports given synthetic EDIDs could not be told
    /// apart.
    /// </summary>
    [Fact]
    public void Synthesize_ProducesABlockThatCanBeStampedWithASerialAfterwards()
    {
        var source = EdidBlock.FromBytes(
            EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000"));

        var mutated = EdidMutator.WithSerialNumber(source, 1002, "880-B", allowDescriptorCreation: true);

        Assert.True(mutated.SerialTextApplied);
        Assert.Equal("880-B", mutated.Edid.SerialText);
        Assert.Equal(1002u, mutated.Edid.SerialNumber);
        Assert.True(mutated.Edid.IsChecksumValid);
    }

    [Fact]
    public void Synthesize_IsDeterministic()
    {
        // A block that changed between runs would look like new hardware to anything caching
        // monitor identity — including this agent's own mapping store.
        var first = EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000");
        var second = EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Synthesize_EncodesTheActiveResolutionInTheDetailedTiming()
    {
        var bytes = EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000");

        int d = EdidBlock.FirstDescriptorOffset;
        int hActive = bytes[d + 2] | ((bytes[d + 4] & 0xF0) << 4);
        int vActive = bytes[d + 5] | ((bytes[d + 7] & 0xF0) << 4);

        Assert.Equal(1920, hActive);
        Assert.Equal(1080, vActive);
    }

    [Fact]
    public void Synthesize_UsesTheDmtPixelClockForTheStandardIndustrialMode()
    {
        var bytes = EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000");

        int pixelClockKhz = (bytes[EdidBlock.FirstDescriptorOffset] |
                             (bytes[EdidBlock.FirstDescriptorOffset + 1] << 8)) * 10;

        Assert.Equal(148_500, pixelClockKhz);
    }

    [Fact]
    public void Synthesize_DeclaresNoExtensionBlocks()
    {
        var bytes = EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000");

        Assert.Equal(0, bytes[EdidBlock.ExtensionCountOffset]);
        Assert.Equal(1, EdidBlock.FromBytes(bytes).BlockCount);
    }

    [Fact]
    public void Synthesize_ReportsAPlausiblePhysicalSize()
    {
        var bytes = EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, "DM7000");

        // 16:9 at ~96 dpi is roughly 51 x 29 cm. An estimate — the real size is unknowable
        // without the panel's own EDID — but it must at least be in range and non-zero.
        Assert.InRange(bytes[21], 30, 80);
        Assert.InRange(bytes[22], 15, 50);
    }

    [Theory]
    [InlineData(0, 1080, 60)]
    [InlineData(4096, 1080, 60)]
    [InlineData(1920, 0, 60)]
    [InlineData(1920, 4096, 60)]
    [InlineData(1920, 1080, 0)]
    [InlineData(1920, 1080, 241)]
    public void Synthesize_RejectsModesThatCannotBeRepresented(int width, int height, int refresh)
    {
        // An EDID detailed timing stores active pixels in 12 bits. Silently truncating would
        // produce a timing the panel cannot drive.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EdidSynthesizer.SynthesizeFromDisplayMode(width, height, refresh, "Test"));
    }

    [Fact]
    public void Synthesize_FallsBackToAnEmptyNameGracefully()
    {
        var edid = EdidBlock.FromBytes(
            EdidSynthesizer.SynthesizeFromDisplayMode(1920, 1080, 60, ""));

        Assert.Equal("Synthetic", edid.MonitorName);
        Assert.True(edid.IsChecksumValid);
    }

    [Fact]
    public void ComputeTiming_ProducesATotalConsistentWithTheRequestedRefresh()
    {
        var timing = EdidSynthesizer.ComputeTiming(1600, 900, 60);

        int hTotal = timing.HActive + timing.HBlank;
        int vTotal = timing.VActive + timing.VBlank;
        double refresh = timing.PixelClockKhz * 1000.0 / (hTotal * (double)vTotal);

        // The fallback rounds the pixel clock to the EDID's 10 kHz granularity, so a couple of
        // Hz of drift is expected and acceptable.
        Assert.InRange(refresh, 58.0, 62.0);
    }
}
