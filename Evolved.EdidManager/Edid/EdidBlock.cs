using System.Text;

namespace Evolved.EdidManager.Edid;

/// <summary>
/// One 128-byte EDID block plus any extension blocks, as read from the monitor's PnP key.
///
/// Nothing here is hard-coded for a particular panel: every field is decoded from the bytes
/// the hardware actually reported, so the same code works for a DM7000, a DM3000 or anything
/// else on the bus.
///
/// Layout references are to VESA E-EDID 1.4, block 0:
///   0..7    fixed header 00 FF FF FF FF FF FF 00
///   8..9    manufacturer id, big-endian, three 5-bit letters
///   10..11  product code, little-endian
///   12..15  serial number, little-endian
///   16      week of manufacture, 17 year - 1990
///   54..125 four 18-byte descriptors
///   126     number of extension blocks
///   127     checksum: all 128 bytes must sum to 0 mod 256
/// </summary>
public sealed class EdidBlock
{
    /// <summary>Size of a single EDID block.</summary>
    public const int BlockSize = 128;

    /// <summary>Offset of the little-endian 32-bit serial number.</summary>
    public const int SerialNumberOffset = 12;

    /// <summary>Offset of the first 18-byte descriptor.</summary>
    public const int FirstDescriptorOffset = 54;

    /// <summary>Number of 18-byte descriptors in block 0.</summary>
    public const int DescriptorCount = 4;

    /// <summary>Length of one descriptor.</summary>
    public const int DescriptorLength = 18;

    /// <summary>Descriptor tag for the serial-number text.</summary>
    public const byte SerialTextDescriptorTag = 0xFF;

    /// <summary>Descriptor tag for the monitor name text.</summary>
    public const byte MonitorNameDescriptorTag = 0xFC;

    /// <summary>Usable characters in a text descriptor before the terminator.</summary>
    public const int DescriptorTextLength = 13;

    /// <summary>Offset of the extension block count in block 0.</summary>
    public const int ExtensionCountOffset = 126;

    /// <summary>Offset of the block checksum.</summary>
    public const int ChecksumOffset = 127;

    private static readonly byte[] Header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    private readonly byte[] _raw;

    private EdidBlock(byte[] raw) => _raw = raw;

    /// <summary>
    /// Wraps a raw EDID byte array. The array is copied, so the caller cannot mutate the block
    /// behind its back.
    /// </summary>
    /// <param name="raw">Complete EDID as read from the registry, including extension blocks.</param>
    /// <exception cref="ArgumentException">If the data is too short or lacks the EDID header.</exception>
    public static EdidBlock FromBytes(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < BlockSize)
            throw new ArgumentException($"EDID must be at least {BlockSize} bytes, got {raw.Length}.", nameof(raw));

        if (!raw[..Header.Length].SequenceEqual(Header))
            throw new ArgumentException("Data does not start with the EDID header.", nameof(raw));

        return new EdidBlock(raw.ToArray());
    }

    /// <summary>
    /// Attempts to wrap a raw EDID, returning false instead of throwing for unusable data.
    /// </summary>
    public static bool TryFromBytes(ReadOnlySpan<byte> raw, out EdidBlock? block)
    {
        try
        {
            block = FromBytes(raw);
            return true;
        }
        catch (ArgumentException)
        {
            block = null;
            return false;
        }
    }

    /// <summary>The complete EDID, block 0 followed by any extension blocks.</summary>
    public ReadOnlySpan<byte> Raw => _raw;

    /// <summary>Total length in bytes, including extension blocks.</summary>
    public int Length => _raw.Length;

    /// <summary>
    /// Number of complete blocks present. Note this counts what is actually in the data, which
    /// can differ from what byte 126 claims if the firmware is inconsistent.
    /// </summary>
    public int BlockCount => _raw.Length / BlockSize;

    /// <summary>Extension block count as declared in byte 126.</summary>
    public byte DeclaredExtensionCount => _raw[ExtensionCountOffset];

    /// <summary>Three-letter PNP manufacturer code, e.g. "CHR" or "IVM".</summary>
    public string ManufacturerCode
    {
        get
        {
            int packed = (_raw[8] << 8) | _raw[9];
            return new string(new[]
            {
                (char)('A' - 1 + ((packed >> 10) & 0x1F)),
                (char)('A' - 1 + ((packed >> 5) & 0x1F)),
                (char)('A' - 1 + (packed & 0x1F))
            });
        }
    }

    /// <summary>Vendor product code (bytes 10-11, little-endian).</summary>
    public ushort ProductCode => (ushort)(_raw[10] | (_raw[11] << 8));

    /// <summary>Numeric serial number (bytes 12-15, little-endian).</summary>
    public uint SerialNumber =>
        (uint)(_raw[12] | (_raw[13] << 8) | (_raw[14] << 16) | (_raw[15] << 24));

    /// <summary>
    /// Serial number text from the 0xFF descriptor, or null when the panel does not provide one.
    /// </summary>
    public string? SerialText => ReadTextDescriptor(SerialTextDescriptorTag);

    /// <summary>Monitor name from the 0xFC descriptor, or null when absent.</summary>
    public string? MonitorName => ReadTextDescriptor(MonitorNameDescriptorTag);

    /// <summary>Checksum byte of block 0 as stored.</summary>
    public byte Checksum => _raw[ChecksumOffset];

    /// <summary>True when every block's bytes sum to zero mod 256, as the standard requires.</summary>
    public bool IsChecksumValid
    {
        get
        {
            for (int block = 0; block < BlockCount; block++)
            {
                if (ComputeChecksum(_raw, block * BlockSize) != _raw[block * BlockSize + ChecksumOffset])
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The identity two panels of the same model share: manufacturer, product and serial. This
    /// is exactly the tuple that collides on structurally identical monitors and makes them
    /// indistinguishable to anything that identifies displays by EDID.
    /// </summary>
    public string IdentityKey =>
        $"{ManufacturerCode}:{ProductCode:X4}:{SerialNumber:X8}:{SerialText ?? string.Empty}";

    /// <summary>Returns a copy of the raw bytes the caller may modify.</summary>
    public byte[] ToArray() => (byte[])_raw.Clone();

    /// <summary>
    /// Computes the checksum byte for the block starting at <paramref name="blockOffset"/>:
    /// the value that makes all 128 bytes sum to zero mod 256.
    /// </summary>
    public static byte ComputeChecksum(ReadOnlySpan<byte> data, int blockOffset = 0)
    {
        int sum = 0;
        for (int i = 0; i < ChecksumOffset; i++)
            sum += data[blockOffset + i];

        return (byte)((256 - (sum % 256)) % 256);
    }

    /// <summary>
    /// Finds the offset of the descriptor carrying the given tag, or -1 when absent.
    /// A text descriptor starts with 00 00 00 &lt;tag&gt; 00.
    /// </summary>
    public int FindDescriptorOffset(byte tag)
    {
        for (int i = 0; i < DescriptorCount; i++)
        {
            int offset = FirstDescriptorOffset + i * DescriptorLength;

            if (_raw[offset] == 0x00 && _raw[offset + 1] == 0x00 &&
                _raw[offset + 2] == 0x00 && _raw[offset + 3] == tag)
            {
                return offset;
            }
        }

        return -1;
    }

    private string? ReadTextDescriptor(byte tag)
    {
        int offset = FindDescriptorOffset(tag);
        if (offset < 0)
            return null;

        // Text starts at descriptor byte 5 and runs 13 bytes, terminated by 0x0A and padded
        // with spaces.
        var text = new StringBuilder(DescriptorTextLength);
        for (int i = 0; i < DescriptorTextLength; i++)
        {
            byte c = _raw[offset + 5 + i];
            if (c == 0x0A)
                break;

            text.Append((char)c);
        }

        return text.ToString().TrimEnd();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{ManufacturerCode} {ProductCode:X4} serial={SerialNumber} " +
        $"text={SerialText ?? "-"} name={MonitorName ?? "-"} " +
        $"blocks={BlockCount} checksum={(IsChecksumValid ? "ok" : "INVALID")}";
}
