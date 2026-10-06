namespace Evolved.EdidManager.Edid;

/// <summary>
/// Builds a minimal but standards-conforming EDID 1.3 block from a display mode.
///
/// LAST RESORT, and deliberately labelled as such. This runs when a range extender passes no
/// DDC through AND no vendor .inf or .bin is available for the panel behind it. The synthesised
/// block describes the mode Windows is currently driving, which is enough for the port to be
/// identified and kept alive — but it is NOT the panel's real EDID. It carries no
/// manufacturer-specific timings, no colour characteristics measured from the panel, and a
/// physical size derived from an assumed aspect ratio.
///
/// Consequences worth stating plainly: colour rendering and DPI-based scaling will be
/// approximate, and any mode the panel supports beyond the synthesised one will be invisible to
/// Windows. Where a real EDID or a vendor INF exists, it is always the better source.
///
/// Timing values follow VESA GTF/DMT conventions for the common industrial resolutions; for
/// anything else a CVT-style blanking estimate is used, which is why the result is a working
/// mode rather than a pixel-exact reproduction of the panel's preferred timing.
/// </summary>
public static class EdidSynthesizer
{
    /// <summary>Manufacturer id stamped into synthesised blocks: "EVD" (Evolved).</summary>
    public const string SyntheticManufacturer = "EVD";

    /// <summary>Product code stamped into synthesised blocks, so they are recognisable as such.</summary>
    public const ushort SyntheticProductCode = 0xE01D;

    /// <summary>
    /// Builds a single 128-byte EDID 1.3 block for the given mode.
    /// </summary>
    /// <param name="width">Horizontal active pixels.</param>
    /// <param name="height">Vertical active pixels.</param>
    /// <param name="refreshRate">Vertical refresh in Hz.</param>
    /// <param name="monitorName">Name for the 0xFC descriptor; truncated to 13 characters.</param>
    /// <param name="serialNumber">Numeric serial for bytes 12-15.</param>
    /// <param name="serialText">Optional text for the 0xFF descriptor.</param>
    /// <exception cref="ArgumentOutOfRangeException">If the mode is not representable in an EDID.</exception>
    public static byte[] SynthesizeFromDisplayMode(
        int width,
        int height,
        int refreshRate,
        string monitorName,
        uint serialNumber = 1,
        string? serialText = null)
    {
        // An EDID detailed timing stores active pixels in 12 bits, so anything beyond 4095 is
        // simply not expressible — failing loudly beats writing a truncated, wrong timing.
        if (width is < 1 or > 4095)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be 1..4095 (EDID stores 12 bits).");

        if (height is < 1 or > 4095)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be 1..4095 (EDID stores 12 bits).");

        if (refreshRate is < 1 or > 240)
            throw new ArgumentOutOfRangeException(nameof(refreshRate), refreshRate, "Refresh rate must be 1..240 Hz.");

        var edid = new byte[EdidBlock.BlockSize];

        WriteHeader(edid);
        WriteVendorAndProduct(edid, serialNumber);
        WriteBasicDisplayParameters(edid, width, height);
        WriteChromaticity(edid);
        WriteEstablishedAndStandardTimings(edid);

        var timing = ComputeTiming(width, height, refreshRate);
        WriteDetailedTiming(edid, EdidBlock.FirstDescriptorOffset, timing);

        // Descriptor 1: range limits — Windows wants them and their absence makes some drivers
        // treat the EDID as incomplete.
        WriteRangeLimits(edid, EdidBlock.FirstDescriptorOffset + EdidBlock.DescriptorLength, refreshRate);

        // Descriptor 2: monitor name.
        WriteTextDescriptor(edid, EdidBlock.FirstDescriptorOffset + 2 * EdidBlock.DescriptorLength,
            EdidBlock.MonitorNameDescriptorTag,
            string.IsNullOrWhiteSpace(monitorName) ? "Synthetic" : monitorName);

        // Descriptor 3: serial text, or a dummy when none was requested. A dummy rather than
        // zeros because 0x10 is the tag the standard defines for an unused slot.
        int lastDescriptor = EdidBlock.FirstDescriptorOffset + 3 * EdidBlock.DescriptorLength;
        if (!string.IsNullOrWhiteSpace(serialText))
            WriteTextDescriptor(edid, lastDescriptor, EdidBlock.SerialTextDescriptorTag, serialText!);
        else
            WriteDummyDescriptor(edid, lastDescriptor);

        edid[EdidBlock.ExtensionCountOffset] = 0;
        edid[EdidBlock.ChecksumOffset] = EdidBlock.ComputeChecksum(edid);

        return edid;
    }

    /// <summary>
    /// Derives blanking intervals for a mode.
    ///
    /// The three resolutions the target installation actually uses get their DMT-standard
    /// timings, because those are what the panels expect. Everything else falls back to a
    /// CVT-style estimate — usable, but an estimate, which is part of why a synthesised EDID is
    /// a last resort.
    /// </summary>
    internal static SyntheticTiming ComputeTiming(int width, int height, int refreshRate)
    {
        // VESA DMT entries for the modes that matter here.
        if (width == 1920 && height == 1080 && refreshRate is 59 or 60)
            return new SyntheticTiming(148_500, 1920, 280, 88, 44, 1080, 45, 4, 5);

        if (width == 1280 && height == 1024 && refreshRate is 59 or 60)
            return new SyntheticTiming(108_000, 1280, 408, 48, 112, 1024, 42, 1, 3);

        if (width == 1024 && height == 768 && refreshRate is 59 or 60)
            return new SyntheticTiming(65_000, 1024, 320, 24, 136, 768, 38, 3, 6);

        // CVT-ish fallback: proportional blanking, then the pixel clock that yields the
        // requested refresh. Rounded to 10 kHz because that is the EDID's storage granularity.
        int hBlank = Math.Max(160, RoundTo(width / 5, 8));
        int vBlank = Math.Max(20, height / 20 + 14);

        int hTotal = width + hBlank;
        int vTotal = height + vBlank;

        long pixelClockKhz = (long)hTotal * vTotal * refreshRate / 1000;
        pixelClockKhz = RoundTo((int)Math.Min(pixelClockKhz, 655_350), 10);

        return new SyntheticTiming(
            (int)pixelClockKhz,
            width, hBlank, RoundTo(hBlank / 4, 8), RoundTo(hBlank / 8, 8),
            height, vBlank, 3, 6);
    }

    private static int RoundTo(int value, int multiple) =>
        multiple <= 1 ? value : (value / multiple) * multiple;

    private static void WriteHeader(byte[] edid)
    {
        edid[0] = 0x00;
        for (int i = 1; i <= 6; i++)
            edid[i] = 0xFF;
        edid[7] = 0x00;
    }

    private static void WriteVendorAndProduct(byte[] edid, uint serialNumber)
    {
        int packed =
            ((SyntheticManufacturer[0] - 'A' + 1) << 10) |
            ((SyntheticManufacturer[1] - 'A' + 1) << 5) |
            (SyntheticManufacturer[2] - 'A' + 1);

        edid[8] = (byte)(packed >> 8);
        edid[9] = (byte)(packed & 0xFF);

        edid[10] = (byte)(SyntheticProductCode & 0xFF);
        edid[11] = (byte)(SyntheticProductCode >> 8);

        edid[12] = (byte)(serialNumber & 0xFF);
        edid[13] = (byte)((serialNumber >> 8) & 0xFF);
        edid[14] = (byte)((serialNumber >> 16) & 0xFF);
        edid[15] = (byte)((serialNumber >> 24) & 0xFF);

        // Manufacture week/year. Fixed rather than "today" so the same inputs always produce
        // the same bytes — a synthesised EDID that changed daily would look like new hardware
        // to anything caching monitor identity.
        edid[16] = 1;
        edid[17] = 34;   // 2024 - 1990

        edid[18] = 1;    // EDID version 1
        edid[19] = 3;    // revision 3
    }

    private static void WriteBasicDisplayParameters(byte[] edid, int width, int height)
    {
        // Digital input, 8 bits per colour, DisplayPort-compatible.
        edid[20] = 0x80;

        // Physical size in cm, derived from the aspect ratio at roughly 96 dpi. An estimate:
        // the real panel size is not knowable without its EDID, and this is the field that
        // makes DPI-based scaling approximate.
        double diagonalInches = Math.Sqrt(width * (double)width + height * (double)height) / 96.0;
        double aspect = width / (double)height;
        double heightInches = diagonalInches / Math.Sqrt(1 + aspect * aspect);
        double widthInches = heightInches * aspect;

        edid[21] = (byte)Math.Clamp((int)Math.Round(widthInches * 2.54), 1, 255);
        edid[22] = (byte)Math.Clamp((int)Math.Round(heightInches * 2.54), 1, 255);

        edid[23] = 120;    // gamma 2.20 -> (2.20 * 100) - 100
        edid[24] = 0x0A;   // RGB colour, preferred timing is the first DTD
    }

    private static void WriteChromaticity(byte[] edid)
    {
        // sRGB primaries. Not measured from the panel — another reason this is a fallback.
        byte[] chromaticity = { 0xEE, 0x91, 0xA3, 0x54, 0x4C, 0x99, 0x26, 0x0F, 0x50, 0x54 };
        Array.Copy(chromaticity, 0, edid, 25, chromaticity.Length);
    }

    private static void WriteEstablishedAndStandardTimings(byte[] edid)
    {
        // Established timings: 640x480@60, 800x600@60, 1024x768@60 — the modes a display is
        // expected to accept regardless.
        edid[35] = 0x21;
        edid[36] = 0x08;
        edid[37] = 0x00;

        // Standard timings: all unused (0x01 0x01 is the "unused" marker).
        for (int i = 38; i <= 53; i++)
            edid[i] = 0x01;
    }

    private static void WriteDetailedTiming(byte[] edid, int offset, SyntheticTiming t)
    {
        edid[offset + 0] = (byte)((t.PixelClockKhz / 10) & 0xFF);
        edid[offset + 1] = (byte)(((t.PixelClockKhz / 10) >> 8) & 0xFF);

        edid[offset + 2] = (byte)(t.HActive & 0xFF);
        edid[offset + 3] = (byte)(t.HBlank & 0xFF);
        edid[offset + 4] = (byte)(((t.HActive >> 8) << 4) | ((t.HBlank >> 8) & 0x0F));

        edid[offset + 5] = (byte)(t.VActive & 0xFF);
        edid[offset + 6] = (byte)(t.VBlank & 0xFF);
        edid[offset + 7] = (byte)(((t.VActive >> 8) << 4) | ((t.VBlank >> 8) & 0x0F));

        edid[offset + 8] = (byte)(t.HSyncOffset & 0xFF);
        edid[offset + 9] = (byte)(t.HSyncWidth & 0xFF);
        edid[offset + 10] = (byte)(((t.VSyncOffset & 0x0F) << 4) | (t.VSyncWidth & 0x0F));
        edid[offset + 11] = (byte)(
            ((t.HSyncOffset >> 8) << 6) |
            ((t.HSyncWidth >> 8) << 4) |
            ((t.VSyncOffset >> 4) << 2) |
            (t.VSyncWidth >> 4));

        // Image size in mm, consistent with the cm figures in the basic parameters block.
        int widthMm = edid[21] * 10;
        int heightMm = edid[22] * 10;
        edid[offset + 12] = (byte)(widthMm & 0xFF);
        edid[offset + 13] = (byte)(heightMm & 0xFF);
        edid[offset + 14] = (byte)(((widthMm >> 8) << 4) | ((heightMm >> 8) & 0x0F));

        edid[offset + 15] = 0;   // horizontal border
        edid[offset + 16] = 0;   // vertical border
        edid[offset + 17] = 0x1E; // non-interlaced, digital separate sync, +H +V
    }

    private static void WriteRangeLimits(byte[] edid, int offset, int refreshRate)
    {
        edid[offset + 0] = 0x00;
        edid[offset + 1] = 0x00;
        edid[offset + 2] = 0x00;
        edid[offset + 3] = 0xFD;   // range limits tag
        edid[offset + 4] = 0x00;

        edid[offset + 5] = (byte)Math.Max(1, refreshRate - 10);   // min vertical Hz
        edid[offset + 6] = (byte)Math.Min(255, refreshRate + 15); // max vertical Hz
        edid[offset + 7] = 30;    // min horizontal kHz
        edid[offset + 8] = 160;   // max horizontal kHz
        edid[offset + 9] = 24;    // max pixel clock / 10 MHz -> 240 MHz
        edid[offset + 10] = 0x00; // no secondary timing formula
        edid[offset + 11] = 0x0A;

        for (int i = 12; i < EdidBlock.DescriptorLength; i++)
            edid[offset + i] = 0x20;
    }

    private static void WriteTextDescriptor(byte[] edid, int offset, byte tag, string text)
    {
        edid[offset + 0] = 0x00;
        edid[offset + 1] = 0x00;
        edid[offset + 2] = 0x00;
        edid[offset + 3] = tag;
        edid[offset + 4] = 0x00;

        int start = offset + 5;
        int i = 0;

        foreach (char c in text)
        {
            if (i >= EdidBlock.DescriptorTextLength)
                break;

            edid[start + i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
            i++;
        }

        if (i < EdidBlock.DescriptorTextLength)
            edid[start + i++] = 0x0A;

        while (i < EdidBlock.DescriptorTextLength)
            edid[start + i++] = 0x20;
    }

    private static void WriteDummyDescriptor(byte[] edid, int offset)
    {
        edid[offset + 3] = 0x10;   // the standard's "unused descriptor" tag
        for (int i = 5; i < EdidBlock.DescriptorLength; i++)
            edid[offset + i] = 0x00;
    }
}

/// <summary>Timing parameters for a synthesised detailed timing descriptor.</summary>
/// <param name="PixelClockKhz">Pixel clock in kHz.</param>
/// <param name="HActive">Horizontal active pixels.</param>
/// <param name="HBlank">Horizontal blanking pixels.</param>
/// <param name="HSyncOffset">Horizontal sync offset (front porch).</param>
/// <param name="HSyncWidth">Horizontal sync pulse width.</param>
/// <param name="VActive">Vertical active lines.</param>
/// <param name="VBlank">Vertical blanking lines.</param>
/// <param name="VSyncOffset">Vertical sync offset (front porch).</param>
/// <param name="VSyncWidth">Vertical sync pulse width.</param>
public sealed record SyntheticTiming(
    int PixelClockKhz,
    int HActive,
    int HBlank,
    int HSyncOffset,
    int HSyncWidth,
    int VActive,
    int VBlank,
    int VSyncOffset,
    int VSyncWidth);
