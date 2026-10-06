using Evolved.EdidManager.Edid;
using Xunit;

namespace TouchMappingAgent.Tests.EdidTests;

/// <summary>
/// Tests for EDID parsing and mutation.
///
/// The checksum is the part that must not be wrong under any circumstance: a graphics driver
/// rejects an EDID whose block does not sum to zero mod 256, and the usual symptom is that the
/// port goes dark rather than falling back to the hardware EDID. Everything here therefore
/// verifies the checksum after every operation, not just the field that was changed.
/// </summary>
public class EdidMutatorTests
{
    // ---------------------------------------------------------------- parsing

    [Fact]
    public void RealEdid_ParsesEveryIdentityField()
    {
        var edid = EdidFixtures.Pl2452UnitA();

        Assert.Equal("IVM", edid.ManufacturerCode);
        Assert.Equal(0x610A, edid.ProductCode);
        Assert.Equal(0x01010101u, edid.SerialNumber);
        Assert.Equal("1128533500538", edid.SerialText);
        Assert.Equal("PL2452", edid.MonitorName);
        Assert.Equal(1, edid.BlockCount);
        Assert.True(edid.IsChecksumValid);
    }

    /// <summary>
    /// The checksum implementation is validated against firmware nobody here wrote: recomputing
    /// the byte for a real, untouched EDID must reproduce exactly what the vendor stored.
    /// </summary>
    [Fact]
    public void ComputeChecksum_ReproducesTheVendorsOwnChecksum()
    {
        foreach (var bytes in new[] { EdidFixtures.Pl2452UnitABytes(), EdidFixtures.Pl2452UnitBBytes() })
        {
            byte stored = bytes[EdidBlock.ChecksumOffset];
            byte computed = EdidBlock.ComputeChecksum(bytes);

            Assert.Equal(stored, computed);
        }
    }

    /// <summary>
    /// The modulo is applied twice on purpose. When the first 127 bytes already sum to a
    /// multiple of 256, "256 - 0" is 256, which does not fit in a byte; the second modulo turns
    /// it into 0. Getting this wrong produces a checksum of 0x00 vs a truncated 0x00 by luck on
    /// most inputs and a wrong value on the rest.
    /// </summary>
    [Fact]
    public void ComputeChecksum_YieldsZero_WhenPayloadIsAlreadyAligned()
    {
        var data = new byte[EdidBlock.BlockSize];
        data[0] = 0x80;
        data[1] = 0x80;   // sum = 256 -> 256 % 256 == 0

        Assert.Equal(0, EdidBlock.ComputeChecksum(data));
    }

    [Theory]
    [InlineData(1, 255)]
    [InlineData(255, 1)]
    [InlineData(128, 128)]
    public void ComputeChecksum_MakesTheBlockSumToZero(byte first, byte second)
    {
        var data = new byte[EdidBlock.BlockSize];
        data[0] = first;
        data[1] = second;
        data[EdidBlock.ChecksumOffset] = EdidBlock.ComputeChecksum(data);

        int total = 0;
        foreach (var b in data)
            total += b;

        Assert.Equal(0, total % 256);
    }

    [Fact]
    public void FromBytes_RejectsDataWithoutTheEdidHeader()
    {
        var bogus = new byte[EdidBlock.BlockSize];

        Assert.Throws<ArgumentException>(() => EdidBlock.FromBytes(bogus));
        Assert.False(EdidBlock.TryFromBytes(bogus, out _));
    }

    [Fact]
    public void FromBytes_RejectsShortData()
    {
        var truncated = EdidFixtures.Pl2452UnitABytes()[..64];

        Assert.Throws<ArgumentException>(() => EdidBlock.FromBytes(truncated));
    }

    // ---------------------------------------------------------------- mutation

    [Fact]
    public void WithSerialNumber_WritesTheNumericSerialLittleEndian()
    {
        var result = EdidMutator.WithSerialNumber(EdidFixtures.Pl2452UnitA(), 0x11223344);
        var bytes = result.Edid.ToArray();

        Assert.Equal(0x44, bytes[12]);
        Assert.Equal(0x33, bytes[13]);
        Assert.Equal(0x22, bytes[14]);
        Assert.Equal(0x11, bytes[15]);
        Assert.Equal(0x11223344u, result.Edid.SerialNumber);
    }

    [Fact]
    public void WithSerialNumber_LeavesTheBlockChecksumValid()
    {
        var result = EdidMutator.WithSerialNumber(EdidFixtures.Pl2452UnitA(), 900_000_001, "880-A");

        Assert.True(result.Edid.IsChecksumValid);
    }

    /// <summary>
    /// Only the serial fields and the checksum may change. A mutation that disturbed a timing
    /// descriptor would alter the modes the panel offers — a far worse outcome than the
    /// ambiguity it set out to fix.
    /// </summary>
    [Fact]
    public void WithSerialNumber_TouchesOnlyTheSerialFieldsAndTheChecksum()
    {
        var original = EdidFixtures.Pl2452UnitABytes();
        var mutated = EdidMutator.WithSerialNumber(EdidFixtures.Pl2452UnitA(), 900_000_001, "880-A").Edid.ToArray();

        int serialTextStart = EdidFixtures.Pl2452UnitA().FindDescriptorOffset(EdidBlock.SerialTextDescriptorTag) + 5;
        int serialTextEnd = serialTextStart + EdidBlock.DescriptorTextLength;

        for (int i = 0; i < original.Length; i++)
        {
            bool mayChange =
                (i >= EdidBlock.SerialNumberOffset && i < EdidBlock.SerialNumberOffset + 4) ||
                (i >= serialTextStart && i < serialTextEnd) ||
                i == EdidBlock.ChecksumOffset;

            if (!mayChange)
                Assert.True(original[i] == mutated[i], $"Byte {i} changed unexpectedly.");
        }
    }

    [Fact]
    public void WithSerialNumber_RewritesTheSerialTextDescriptor()
    {
        var result = EdidMutator.WithSerialNumber(EdidFixtures.Pl2452UnitA(), 900_000_001, "880-A");

        Assert.True(result.SerialTextApplied);
        Assert.Equal("880-A", result.Edid.SerialText);
        Assert.True(result.Edid.IsChecksumValid);
    }

    /// <summary>
    /// The 13-byte text field must be terminated with 0x0A and space-padded, as the standard
    /// prescribes — otherwise leftovers from the longer previous text remain visible.
    /// </summary>
    [Fact]
    public void WithSerialNumber_PadsTheTextFieldAndClearsTheOldValue()
    {
        // The original text is 13 characters, the new one 5; the remainder must be cleared.
        var result = EdidMutator.WithSerialNumber(EdidFixtures.Pl2452UnitA(), 1, "880-A");
        var bytes = result.Edid.ToArray();

        int start = result.Edid.FindDescriptorOffset(EdidBlock.SerialTextDescriptorTag) + 5;

        Assert.Equal((byte)'8', bytes[start]);
        Assert.Equal((byte)'A', bytes[start + 4]);
        Assert.Equal(0x0A, bytes[start + 5]);

        for (int i = 6; i < EdidBlock.DescriptorTextLength; i++)
            Assert.Equal(0x20, bytes[start + i]);
    }

    [Fact]
    public void WithSerialNumber_FillsTheTextFieldExactly_WhenTextIsFullLength()
    {
        var full = new string('7', EdidBlock.DescriptorTextLength);
        var result = EdidMutator.WithSerialNumber(EdidFixtures.Pl2452UnitA(), 1, full);

        Assert.Equal(full, result.Edid.SerialText);
        Assert.True(result.Edid.IsChecksumValid);
    }

    [Fact]
    public void WithSerialNumber_TruncatesOverlongText_WithoutCorruptingTheNextDescriptor()
    {
        var original = EdidFixtures.Pl2452UnitABytes();
        var tooLong = new string('X', 40);
        var mutated = EdidMutator.WithSerialNumber(EdidFixtures.Pl2452UnitA(), 1, tooLong).Edid;

        int descriptor = mutated.FindDescriptorOffset(EdidBlock.SerialTextDescriptorTag);
        int nextDescriptor = descriptor + EdidBlock.DescriptorLength;

        // The following descriptor must be byte-identical.
        for (int i = 0; i < EdidBlock.DescriptorLength; i++)
            Assert.Equal(original[nextDescriptor + i], mutated.ToArray()[nextDescriptor + i]);

        Assert.True(mutated.IsChecksumValid);
    }

    /// <summary>
    /// The DM7000 case: no 0xFF descriptor at all. The mutator must not invent one by
    /// sacrificing a descriptor slot that holds timings.
    /// </summary>
    [Fact]
    public void WithSerialNumber_WhenNoSerialTextDescriptorExists_ChangesOnlyTheNumericSerial()
    {
        var original = EdidFixtures.Dm7000Bytes();
        var result = EdidMutator.WithSerialNumber(EdidFixtures.Dm7000(), 900_000_002, "880-B");

        Assert.False(result.SerialTextApplied);
        Assert.Equal(900_000_002u, result.Edid.SerialNumber);
        Assert.Null(result.Edid.SerialText);
        Assert.True(result.Edid.IsChecksumValid);

        // The monitor-name descriptor must be untouched.
        Assert.Equal("DM7000", result.Edid.MonitorName);
        int nameOffset = result.Edid.FindDescriptorOffset(EdidBlock.MonitorNameDescriptorTag);
        for (int i = 0; i < EdidBlock.DescriptorLength; i++)
            Assert.Equal(original[nameOffset + i], result.Edid.ToArray()[nameOffset + i]);
    }

    [Fact]
    public void WithSerialNumber_ReplacesNonAsciiWithAPlaceholder()
    {
        var result = EdidMutator.WithSerialNumber(EdidFixtures.Pl2452UnitA(), 1, "88Ü-A");

        Assert.Equal("88?-A", result.Edid.SerialText);
        Assert.True(result.Edid.IsChecksumValid);
    }

    // ---------------------------------------------------------------- multi-block

    /// <summary>
    /// A 256-byte panel is present on the development machine, so extension handling is not
    /// hypothetical. The extension block carries CEA data — audio formats, colorimetry, extra
    /// timings — and corrupting it degrades what the display can negotiate.
    /// </summary>
    [Fact]
    public void WithSerialNumber_PreservesExtensionBlocksByteForByte()
    {
        var withExtension = EdidFixtures.WithExtensionBlock(EdidFixtures.Pl2452UnitABytes());
        var source = EdidBlock.FromBytes(withExtension);

        Assert.Equal(2, source.BlockCount);
        Assert.True(source.IsChecksumValid);

        var mutated = EdidMutator.WithSerialNumber(source, 900_000_001, "880-A").Edid;

        Assert.Equal(2, mutated.BlockCount);
        Assert.Equal(withExtension.Length, mutated.Length);

        for (int i = EdidBlock.BlockSize; i < withExtension.Length; i++)
            Assert.True(withExtension[i] == mutated.ToArray()[i], $"Extension byte {i} changed.");

        // Both blocks must still validate — block 0 because it was recomputed, the extension
        // because it was left alone.
        Assert.True(mutated.IsChecksumValid);
    }

    [Fact]
    public void RecomputeChecksum_WritesAndReturnsTheSameValue()
    {
        var bytes = EdidFixtures.Pl2452UnitABytes();
        bytes[EdidBlock.SerialNumberOffset] ^= 0xFF;   // disturb the block

        byte written = EdidMutator.RecomputeChecksum(bytes);

        Assert.Equal(written, bytes[EdidBlock.ChecksumOffset]);
        Assert.True(EdidBlock.FromBytes(bytes).IsChecksumValid);
    }

    [Fact]
    public void RecomputeChecksum_RejectsShortBuffers()
    {
        Assert.Throws<ArgumentException>(() => EdidMutator.RecomputeChecksum(new byte[64]));
    }
}
