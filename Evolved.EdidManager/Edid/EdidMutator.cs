namespace Evolved.EdidManager.Edid;

/// <summary>
/// Produces a modified clone of an EDID with a new serial number.
///
/// Pure and side-effect free: it takes bytes and returns bytes. Nothing here touches the
/// registry or the device tree, which is what makes the correctness-critical part — the
/// checksum arithmetic and the descriptor rewrite — directly testable.
///
/// Only block 0 is modified. Extension blocks are copied through untouched, along with their
/// own checksums, because the serial number lives in block 0 only.
/// </summary>
public static class EdidMutator
{
    /// <summary>
    /// Returns a clone of <paramref name="source"/> carrying the given serial number, with
    /// block 0's checksum recomputed.
    /// </summary>
    /// <param name="source">The original EDID.</param>
    /// <param name="serialNumber">New numeric serial for bytes 12-15.</param>
    /// <param name="serialText">
    /// New text for the 0xFF descriptor. When null the descriptor is left alone; when the panel
    /// has no 0xFF descriptor the text is ignored (see <see cref="MutationResult.SerialTextApplied"/>).
    /// </param>
    /// <param name="allowDescriptorCreation">
    /// When true and the panel has no 0xFF descriptor, an UNUSED descriptor slot may be
    /// converted into one. Only a slot that carries no information is eligible — see
    /// <see cref="FindReusableDescriptorOffset"/>.
    /// </param>
    public static MutationResult WithSerialNumber(
        EdidBlock source,
        uint serialNumber,
        string? serialText = null,
        bool allowDescriptorCreation = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        var bytes = source.ToArray();

        WriteSerialNumber(bytes, serialNumber);

        bool textApplied = false;
        if (serialText != null)
        {
            int offset = source.FindDescriptorOffset(EdidBlock.SerialTextDescriptorTag);

            if (offset < 0 && allowDescriptorCreation)
            {
                offset = FindReusableDescriptorOffset(bytes);
                if (offset >= 0)
                {
                    // Turn the empty slot into a serial-text descriptor: 00 00 00 FF 00.
                    bytes[offset] = 0x00;
                    bytes[offset + 1] = 0x00;
                    bytes[offset + 2] = 0x00;
                    bytes[offset + 3] = EdidBlock.SerialTextDescriptorTag;
                    bytes[offset + 4] = 0x00;
                }
            }

            textApplied = TryWriteSerialText(bytes, offset, serialText);
        }

        // MUST come last: every byte written above feeds into it. Writing the checksum before
        // the payload — or forgetting it — yields an EDID the graphics driver rejects outright,
        // which typically means the port goes dark rather than falling back gracefully.
        bytes[EdidBlock.ChecksumOffset] = EdidBlock.ComputeChecksum(bytes);

        return new MutationResult(EdidBlock.FromBytes(bytes), textApplied);
    }

    /// <summary>
    /// Recomputes block 0's checksum in place and returns the value written. Exposed separately
    /// because callers that patch bytes by hand still have to close the block correctly.
    /// </summary>
    public static byte RecomputeChecksum(byte[] edid)
    {
        ArgumentNullException.ThrowIfNull(edid);

        if (edid.Length < EdidBlock.BlockSize)
            throw new ArgumentException($"EDID must be at least {EdidBlock.BlockSize} bytes.", nameof(edid));

        byte checksum = EdidBlock.ComputeChecksum(edid);
        edid[EdidBlock.ChecksumOffset] = checksum;
        return checksum;
    }

    /// <summary>
    /// Finds a descriptor slot that carries no information and may therefore be repurposed,
    /// or -1 when every slot is in use.
    ///
    /// A slot is eligible only when it is a DISPLAY descriptor (bytes 0-2 are zero — a detailed
    /// timing has a non-zero pixel clock there) whose tag is 0x00 (undefined) or 0x10 (the
    /// standard "dummy" filler). Anything else holds timings, range limits, the monitor name or
    /// vendor data, and overwriting it would cost the panel information it needs to negotiate a
    /// mode. The real DM7000 EDID has exactly one such empty slot, which is why this is worth
    /// doing rather than giving up on the serial text.
    /// </summary>
    public static int FindReusableDescriptorOffset(ReadOnlySpan<byte> edid)
    {
        for (int i = 0; i < EdidBlock.DescriptorCount; i++)
        {
            int offset = EdidBlock.FirstDescriptorOffset + i * EdidBlock.DescriptorLength;

            if (edid[offset] != 0x00 || edid[offset + 1] != 0x00 || edid[offset + 2] != 0x00)
                continue;   // detailed timing

            byte tag = edid[offset + 3];
            if (tag is 0x00 or 0x10)
                return offset;
        }

        return -1;
    }

    private static void WriteSerialNumber(byte[] bytes, uint serialNumber)
    {
        bytes[EdidBlock.SerialNumberOffset + 0] = (byte)(serialNumber & 0xFF);
        bytes[EdidBlock.SerialNumberOffset + 1] = (byte)((serialNumber >> 8) & 0xFF);
        bytes[EdidBlock.SerialNumberOffset + 2] = (byte)((serialNumber >> 16) & 0xFF);
        bytes[EdidBlock.SerialNumberOffset + 3] = (byte)((serialNumber >> 24) & 0xFF);
    }

    /// <summary>
    /// Rewrites the 13-byte text of the 0xFF descriptor: ASCII, terminated with 0x0A when
    /// shorter than the field, then space-padded — the encoding the standard prescribes.
    ///
    /// Deliberately does NOT create the descriptor when absent. The four descriptor slots are
    /// already occupied by timings and the monitor name; sacrificing one to add a serial string
    /// would discard information the panel needs to negotiate a mode.
    /// </summary>
    private static bool TryWriteSerialText(byte[] bytes, int descriptorOffset, string serialText)
    {
        if (descriptorOffset < 0)
            return false;

        int textStart = descriptorOffset + 5;
        int written = 0;

        foreach (char c in serialText)
        {
            if (written >= EdidBlock.DescriptorTextLength)
                break;

            // Non-ASCII cannot be represented in an EDID text descriptor.
            bytes[textStart + written] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
            written++;
        }

        if (written < EdidBlock.DescriptorTextLength)
        {
            bytes[textStart + written] = 0x0A;
            written++;
        }

        while (written < EdidBlock.DescriptorTextLength)
        {
            bytes[textStart + written] = 0x20;
            written++;
        }

        return true;
    }
}

/// <summary>
/// Outcome of a mutation: the new EDID, plus whether the serial text could be applied.
/// </summary>
/// <param name="Edid">The modified EDID with a valid checksum.</param>
/// <param name="SerialTextApplied">
/// False when the panel has no 0xFF descriptor, so only the numeric serial changed.
/// </param>
public sealed record MutationResult(EdidBlock Edid, bool SerialTextApplied);
