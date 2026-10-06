using System.Text;
using Evolved.EdidManager.Edid;
using Evolved.EdidManager.Templates;
using Xunit;

namespace TouchMappingAgent.Tests.EdidTests;

/// <summary>
/// Tests for extracting EDIDs from Windows monitor drivers.
///
/// An INF is the one place a vendor publishes a corrected EDID for a panel, so behind an
/// extender that swallows DDC it is often the only authoritative source available. The parsing
/// rules that matter are line continuation (a 128-byte block is always wrapped across many
/// lines) and the several punctuation styles real INFs use.
/// </summary>
public class InfEdidParserTests
{
    private static string ToInfHex(byte[] bytes, int perLine = 16)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i += perLine)
        {
            var chunk = bytes.Skip(i).Take(perLine).Select(b => b.ToString("X2"));
            sb.Append(string.Join(",", chunk));

            if (i + perLine < bytes.Length)
                sb.AppendLine(",\\");
        }

        return sb.ToString();
    }

    private static string BuildInf(byte[] block0, byte[]? block1 = null, string section = "DM7000.AddReg")
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Version]");
        sb.AppendLine("Signature=\"$WINDOWS NT$\"");
        sb.AppendLine("Class=Monitor");
        sb.AppendLine();
        sb.AppendLine($"[{section}]");
        sb.AppendLine("HKR,\"MODES\\1920,1080\",Mode1,,\"30.0-83.0,56.0-76.0,+,+\"");
        sb.AppendLine($"HKR, EDID_OVERRIDE, \"0\", 1, {ToInfHex(block0)}");

        if (block1 != null)
            sb.AppendLine($"HKR, EDID_OVERRIDE, \"1\", 1, {ToInfHex(block1)}");

        return sb.ToString();
    }

    [Fact]
    public void Parse_ExtractsASingleBlockAcrossContinuedLines()
    {
        var inf = BuildInf(EdidFixtures.Pl2452UnitABytes());

        var entries = InfEdidParser.Parse(inf);

        var entry = Assert.Single(entries);
        Assert.Equal("DM7000.AddReg", entry.SectionName);
        Assert.Equal(EdidFixtures.Pl2452UnitABytes(), entry.Edid.ToArray());
        Assert.True(entry.IsChecksumValid);
    }

    /// <summary>
    /// A CEA extension arrives as a second numbered value and must come back as one 256-byte
    /// EDID, not two fragments.
    /// </summary>
    [Fact]
    public void Parse_ConcatenatesMultipleBlocksInIndexOrder()
    {
        var twoBlocks = EdidFixtures.WithExtensionBlock(EdidFixtures.Pl2452UnitABytes());
        var block0 = twoBlocks[..EdidBlock.BlockSize];
        var block1 = twoBlocks[EdidBlock.BlockSize..];

        var entries = InfEdidParser.Parse(BuildInf(block0, block1));

        var entry = Assert.Single(entries);
        Assert.Equal(2, entry.Edid.BlockCount);
        Assert.Equal(twoBlocks, entry.Edid.ToArray());
    }

    [Fact]
    public void Parse_HandlesBlocksDeclaredOutOfOrder()
    {
        var twoBlocks = EdidFixtures.WithExtensionBlock(EdidFixtures.Pl2452UnitABytes());
        var block0 = twoBlocks[..EdidBlock.BlockSize];
        var block1 = twoBlocks[EdidBlock.BlockSize..];

        var inf =
            "[Panel.AddReg]\r\n" +
            $"HKR, EDID_OVERRIDE, \"1\", 1, {ToInfHex(block1)}\r\n" +
            $"HKR, EDID_OVERRIDE, \"0\", 1, {ToInfHex(block0)}\r\n";

        var entry = Assert.Single(InfEdidParser.Parse(inf));

        Assert.Equal(twoBlocks, entry.Edid.ToArray());
    }

    /// <summary>
    /// A gap in the block indices means the file is truncated or hand-edited. Concatenating
    /// across it would silently produce an EDID that is not what the vendor published.
    /// </summary>
    [Fact]
    public void Parse_SkipsSectionsWithNonContiguousBlockIndices()
    {
        var block = EdidFixtures.Pl2452UnitABytes();
        var inf =
            "[Panel.AddReg]\r\n" +
            $"HKR, EDID_OVERRIDE, \"1\", 1, {ToInfHex(block)}\r\n";   // starts at 1, no 0

        Assert.Empty(InfEdidParser.Parse(inf));
    }

    [Theory]
    [InlineData("HKR, EDID_OVERRIDE, \"0\", 1, ")]
    [InlineData("HKR,\"EDID_OVERRIDE\",\"0\",1,")]
    [InlineData("HKR,EDID_OVERRIDE,0,1,")]
    [InlineData("hkr, edid_override, \"0\", 0x00000001, ")]
    public void Parse_AcceptsThePunctuationStylesRealInfsUse(string prefix)
    {
        var inf = "[Panel.AddReg]\r\n" + prefix + ToInfHex(EdidFixtures.Pl2452UnitABytes()) + "\r\n";

        var entry = Assert.Single(InfEdidParser.Parse(inf));

        Assert.Equal(EdidFixtures.Pl2452UnitABytes(), entry.Edid.ToArray());
    }

    /// <summary>
    /// Some INFs fold the block index into the subkey path instead of the value name.
    /// </summary>
    [Fact]
    public void Parse_AcceptsTheBlockIndexInTheSubkeyPath()
    {
        var inf =
            "[Panel.AddReg]\r\n" +
            $"HKR,\"EDID_OVERRIDE\\0\",,1,{ToInfHex(EdidFixtures.Pl2452UnitABytes())}\r\n";

        var entry = Assert.Single(InfEdidParser.Parse(inf));

        Assert.Equal(EdidFixtures.Pl2452UnitABytes(), entry.Edid.ToArray());
    }

    [Fact]
    public void Parse_KeepsSectionsApart()
    {
        var inf =
            BuildInf(EdidFixtures.Pl2452UnitABytes(), section: "PanelA.AddReg") +
            BuildInf(EdidFixtures.Pl2452UnitBBytes(), section: "PanelB.AddReg");

        var entries = InfEdidParser.Parse(inf);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.SectionName == "PanelA.AddReg");
        Assert.Contains(entries, e => e.SectionName == "PanelB.AddReg");
        Assert.Equal(2, entries.Select(e => e.Edid.SerialText).Distinct().Count());
    }

    [Fact]
    public void Parse_IgnoresNonEdidAddRegLines()
    {
        var inf =
            "[Panel.AddReg]\r\n" +
            "HKR,\"MODES\\1920,1080\",Mode1,,\"30.0-83.0,56.0-76.0,+,+\"\r\n" +
            "HKR,, \"SomeValue\", 1, 00,01,02\r\n" +
            $"HKR, EDID_OVERRIDE, \"0\", 1, {ToInfHex(EdidFixtures.Pl2452UnitABytes())}\r\n";

        var entry = Assert.Single(InfEdidParser.Parse(inf));

        Assert.Equal(EdidFixtures.Pl2452UnitABytes(), entry.Edid.ToArray());
    }

    [Fact]
    public void Parse_IgnoresCommentedOutBlocks()
    {
        var inf =
            "[Panel.AddReg]\r\n" +
            $"; HKR, EDID_OVERRIDE, \"0\", 1, {ToInfHex(EdidFixtures.Pl2452UnitBBytes())}\r\n" +
            $"HKR, EDID_OVERRIDE, \"0\", 1, {ToInfHex(EdidFixtures.Pl2452UnitABytes())}\r\n";

        var entry = Assert.Single(InfEdidParser.Parse(inf));

        Assert.Equal("1128533500538", entry.Edid.SerialText);
    }

    [Fact]
    public void StripComment_LeavesSemicolonsInsideQuotesAlone()
    {
        // Whitespace is not trimmed here — the caller does that — so the expectation keeps the
        // trailing space that preceded the comment.
        Assert.Equal("HKR,\"a;b\",c ", InfEdidParser.StripComment("HKR,\"a;b\",c ; trailing"));
    }

    [Fact]
    public void Parse_ReportsAnInvalidChecksumInsteadOfDiscardingTheBlock()
    {
        var broken = EdidFixtures.Pl2452UnitABytes();
        broken[EdidBlock.ChecksumOffset] ^= 0xFF;

        var entry = Assert.Single(InfEdidParser.Parse(BuildInf(broken)));

        Assert.False(entry.IsChecksumValid);
    }

    [Fact]
    public void Parse_RejectsABlockOfTheWrongLength()
    {
        var inf =
            "[Panel.AddReg]\r\n" +
            "HKR, EDID_OVERRIDE, \"0\", 1, 00,FF,FF,FF\r\n";

        Assert.Empty(InfEdidParser.Parse(inf));
    }

    [Fact]
    public void Parse_RejectsDataThatIsNotAnEdid()
    {
        var notEdid = new byte[EdidBlock.BlockSize];
        notEdid[127] = EdidBlock.ComputeChecksum(notEdid);

        Assert.Empty(InfEdidParser.Parse(BuildInf(notEdid)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[Version]\r\nSignature=\"$WINDOWS NT$\"\r\n")]
    public void Parse_ReturnsNothingForInputWithoutAnEdid(string inf)
    {
        Assert.Empty(InfEdidParser.Parse(inf));
    }

    [Fact]
    public void SplitContinuedLines_JoinsBackslashContinuations()
    {
        var joined = InfEdidParser.SplitContinuedLines("a,\\\r\nb,\\\r\nc\r\nd").ToList();

        Assert.Equal(2, joined.Count);
        Assert.Equal("a,b,c", joined[0]);
        Assert.Equal("d", joined[1]);
    }

    [Fact]
    public void ParseFile_ReadsAnInfWithAUtf16Bom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"edidtest_{Guid.NewGuid():N}.inf");
        try
        {
            File.WriteAllText(path, BuildInf(EdidFixtures.Pl2452UnitABytes()), Encoding.Unicode);

            var entry = Assert.Single(InfEdidParser.ParseFile(path));

            Assert.Equal(EdidFixtures.Pl2452UnitABytes(), entry.Edid.ToArray());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ParseFile_ReturnsNothingForAMissingFile()
    {
        Assert.Empty(InfEdidParser.ParseFile(Path.Combine(Path.GetTempPath(), "does-not-exist.inf")));
    }
}
