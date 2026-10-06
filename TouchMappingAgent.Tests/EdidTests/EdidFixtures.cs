using Evolved.EdidManager.Edid;

namespace TouchMappingAgent.Tests.EdidTests;

/// <summary>
/// EDID test data.
///
/// The two PL2452 blocks are REAL bytes read from
/// HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\IVM610A\...\Device Parameters\EDID on the
/// development machine. Using real firmware rather than hand-written bytes matters here: an
/// EDID assembled to suit the parser proves only that the parser agrees with itself, whereas
/// these carry the quirks actual panels ship — a placeholder numeric serial of 0x01010101, a
/// 0xFF text descriptor that does the real distinguishing, and a checksum computed by the
/// vendor rather than by this code.
///
/// The DM7000 block is synthesised from the hardware survey of the target installation
/// (manufacturer CHR, product 35088, serial 880, no 0xFF descriptor) because that hardware is
/// not attached here. It is built through the same helpers under test, so it is used only for
/// collision-detection scenarios, never as evidence that parsing is correct.
/// </summary>
internal static class EdidFixtures
{
    /// <summary>
    /// iiyama PL2452, connector UID256. Numeric serial 0x01010101, serial text "1128533500538".
    /// </summary>
    internal const string Pl2452UnitAHex =
        "00FFFFFFFFFFFF0026CD0A61010101012317010380341D782A2CC5A45650A1280F5054BFEF80" +
        "B300A940950081408180950F714FD1C0023A801871382D40582C450009252100001E000000FF" +
        "0031313238353333353030353338000000FD00374C1E5311000A202020202020000000FC0050" +
        "4C323435320A2020202020200070";

    /// <summary>
    /// iiyama PL2452, connector UID65793. Same numeric serial, serial text "1128533500607".
    /// </summary>
    internal const string Pl2452UnitBHex =
        "00FFFFFFFFFFFF0026CD0A61010101012317010368341D782A2CC5A45650A1280F5054BFEF80" +
        "B300A940950081408180950F714FD1C0023A801871382D40582C450009252100001E000000FF" +
        "0031313238353333353030363037000000FD00374C1E5311000A202020202020000000FC0050" +
        "4C323435320A202020202020008B";

    internal const string Pl2452UnitAInstanceId = @"DISPLAY\IVM610A\8&abf64df&0&UID256";
    internal const string Pl2452UnitBInstanceId = @"DISPLAY\IVM610A\4&35aefc14&2&UID65793";

    internal const string Dm7000UnitAInstanceId = @"DISPLAY\CHR8910\5&2c72b841&0&UID250116";
    internal const string Dm7000UnitBInstanceId = @"DISPLAY\CHR8910\5&2c72b841&0&UID250118";

    internal static byte[] Pl2452UnitABytes() => Convert.FromHexString(Pl2452UnitAHex);

    internal static byte[] Pl2452UnitBBytes() => Convert.FromHexString(Pl2452UnitBHex);

    internal static EdidBlock Pl2452UnitA() => EdidBlock.FromBytes(Pl2452UnitABytes());

    internal static EdidBlock Pl2452UnitB() => EdidBlock.FromBytes(Pl2452UnitBBytes());

    /// <summary>
    /// Builds a DM7000-like block: manufacturer CHR, product 35088, serial 880, a 0xFC monitor
    /// name and — crucially for the collision scenario — NO 0xFF serial-text descriptor.
    /// </summary>
    internal static byte[] Dm7000Bytes()
    {
        var edid = new byte[EdidBlock.BlockSize];

        // Header
        edid[0] = 0x00;
        for (int i = 1; i <= 6; i++) edid[i] = 0xFF;
        edid[7] = 0x00;

        // Manufacturer "CHR": three 5-bit letters, big-endian.
        int packed = (('C' - 'A' + 1) << 10) | (('H' - 'A' + 1) << 5) | ('R' - 'A' + 1);
        edid[8] = (byte)(packed >> 8);
        edid[9] = (byte)(packed & 0xFF);

        // Product code 35088 (0x8910), little-endian
        edid[10] = 0x10;
        edid[11] = 0x89;

        // Serial 880, little-endian
        edid[12] = 880 & 0xFF;
        edid[13] = (880 >> 8) & 0xFF;
        edid[14] = 0;
        edid[15] = 0;

        edid[16] = 1;    // week
        edid[17] = 27;   // 2017 - 1990
        edid[18] = 1;    // EDID 1.4
        edid[19] = 4;

        // Descriptor 3: monitor name "DM7000". No 0xFF descriptor anywhere — that absence is
        // the point of this fixture.
        int nameOffset = EdidBlock.FirstDescriptorOffset + 3 * EdidBlock.DescriptorLength;
        edid[nameOffset + 3] = EdidBlock.MonitorNameDescriptorTag;
        WriteDescriptorText(edid, nameOffset, "DM7000");

        edid[EdidBlock.ChecksumOffset] = EdidBlock.ComputeChecksum(edid);
        return edid;
    }

    internal static EdidBlock Dm7000() => EdidBlock.FromBytes(Dm7000Bytes());

    /// <summary>
    /// Appends a CEA-861 style extension block, so multi-block handling can be exercised the
    /// way a 256-byte panel exercises it. The extension carries a recognisable marker so a test
    /// can prove it survived untouched.
    /// </summary>
    internal static byte[] WithExtensionBlock(byte[] block0, byte marker = 0xA5)
    {
        var result = new byte[block0.Length + EdidBlock.BlockSize];
        Array.Copy(block0, result, block0.Length);

        int ext = block0.Length;
        result[ext + 0] = 0x02;   // CEA extension tag
        result[ext + 1] = 0x03;   // revision
        result[ext + 2] = 0x04;   // DTD offset

        for (int i = 4; i < EdidBlock.ChecksumOffset; i++)
            result[ext + i] = marker;

        // Block 0 must declare the extension, and both blocks need valid checksums.
        result[EdidBlock.ExtensionCountOffset] = 1;
        result[EdidBlock.ChecksumOffset] = EdidBlock.ComputeChecksum(result);
        result[ext + EdidBlock.ChecksumOffset] = EdidBlock.ComputeChecksum(result, ext);

        return result;
    }

    private static void WriteDescriptorText(byte[] edid, int descriptorOffset, string text)
    {
        int start = descriptorOffset + 5;
        int i = 0;

        for (; i < text.Length && i < EdidBlock.DescriptorTextLength; i++)
            edid[start + i] = (byte)text[i];

        if (i < EdidBlock.DescriptorTextLength)
            edid[start + i++] = 0x0A;

        while (i < EdidBlock.DescriptorTextLength)
            edid[start + i++] = 0x20;
    }
}
