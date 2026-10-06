using System.Text;
using Evolved.EdidManager.Edid;
using Evolved.EdidManager.Templates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TouchMappingAgent.Tests.EdidTests;

/// <summary>
/// Tests for the EDID template store.
///
/// The store's real job is refusing bad templates. A template file is hand-edited, copied
/// between machines, or produced by a tool nobody verified — it is precisely where a block with
/// a wrong checksum enters the system, and a graphics driver answers such a block by leaving
/// the port dark. Most of what follows is therefore about rejection, not loading.
/// </summary>
public class EdidTemplateStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly EdidTemplateStore _store;

    public EdidTemplateStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "EdidTemplateTests", Guid.NewGuid().ToString("N"));
        _store = new EdidTemplateStore(NullLogger<EdidTemplateStore>.Instance, _directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best effort.
        }

        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- built-in template

    /// <summary>
    /// The built-in DM7000 template must be the panel's REAL EDID. The block originally
    /// proposed for this role identified as manufacturer CSR, product 0, with checksum 0x30
    /// where 0xE0 was required — it would have darkened the port and, by changing the
    /// manufacturer and product code, moved the monitor to a different PnP hardware id, which
    /// invalidates every stored touch mapping.
    /// </summary>
    [Fact]
    public void BuiltInDm7000Template_MatchesTheHardwareThatWasMeasured()
    {
        var edid = EdidTemplateStore.BuiltInDm7000.Edid;

        Assert.Equal("CHR", edid.ManufacturerCode);
        Assert.Equal(0x8910, edid.ProductCode);
        Assert.Equal(880u, edid.SerialNumber);
        Assert.Equal("DM7000", edid.MonitorName);
        Assert.True(edid.IsChecksumValid);
    }

    [Fact]
    public void BuiltInDm7000Template_IsExactlyOneBlock()
    {
        Assert.Equal(EdidBlock.BlockSize, EdidTemplateStore.BuiltInDm7000.Edid.Length);
        Assert.Equal(1, EdidTemplateStore.BuiltInDm7000.Edid.BlockCount);
    }

    /// <summary>
    /// The DM7000 has no 0xFF descriptor — that absence is exactly why two of them are
    /// indistinguishable. It does have one empty descriptor slot, which is what makes stamping
    /// a serial text possible at all.
    /// </summary>
    [Fact]
    public void BuiltInDm7000Template_HasNoSerialTextButAnEmptyDescriptorSlot()
    {
        var edid = EdidTemplateStore.BuiltInDm7000.Edid;

        Assert.Null(edid.SerialText);
        Assert.True(EdidMutator.FindReusableDescriptorOffset(edid.Raw) >= 0);
    }

    [Fact]
    public void ListTemplates_OffersTheBuiltInTemplateEvenWithNoDirectory()
    {
        var templates = _store.ListTemplates();

        Assert.Contains(templates, t => t.Name == EdidTemplateStore.Dm7000TemplateName && t.IsBuiltIn);
    }

    [Fact]
    public void EnsureSeeded_WritesTheBuiltInTemplateToDisk()
    {
        _store.EnsureSeeded();

        var path = Path.Combine(_directory, EdidTemplateStore.Dm7000TemplateName);
        Assert.True(File.Exists(path));

        var loaded = _store.LoadTemplate(EdidTemplateStore.Dm7000TemplateName);
        Assert.NotNull(loaded);
        Assert.Equal("CHR", loaded!.Edid.ManufacturerCode);
        Assert.False(loaded.IsBuiltIn);   // now backed by a file
    }

    [Fact]
    public void EnsureSeeded_IsIdempotentAndDoesNotOverwriteAnEditedTemplate()
    {
        _store.EnsureSeeded();

        // An operator replaces the seeded file with their own valid block.
        var custom = EdidFixtures.Pl2452UnitABytes();
        File.WriteAllBytes(Path.Combine(_directory, EdidTemplateStore.Dm7000TemplateName), custom);

        _store.EnsureSeeded();

        Assert.Equal(custom, File.ReadAllBytes(Path.Combine(_directory, EdidTemplateStore.Dm7000TemplateName)));
    }

    // ---------------------------------------------------------------- loading

    [Fact]
    public void LoadFromFile_ReadsABinaryTemplate()
    {
        var path = Path.Combine(_directory, "panel.bin");
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(path, EdidFixtures.Pl2452UnitABytes());

        var template = _store.LoadFromFile(path);

        Assert.NotNull(template);
        Assert.Equal("IVM", template!.Edid.ManufacturerCode);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("spaced")]
    [InlineData("prefixed")]
    [InlineData("multiline")]
    public void LoadFromFile_ReadsHexTemplatesInTheFormatsTheyActuallyArriveIn(string flavour)
    {
        var hex = Convert.ToHexString(EdidFixtures.Pl2452UnitABytes());

        string content = flavour switch
        {
            "plain" => hex,
            "spaced" => string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2))),
            "prefixed" => string.Join(", ", Enumerable.Range(0, hex.Length / 2).Select(i => "0x" + hex.Substring(i * 2, 2))),
            _ => InsertLineBreaks(hex)
        };

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "panel.hex");
        File.WriteAllText(path, content, Encoding.UTF8);

        var template = _store.LoadFromFile(path);

        Assert.NotNull(template);
        Assert.Equal(EdidFixtures.Pl2452UnitABytes(), template!.Edid.ToArray());
    }

    [Fact]
    public void LoadFromFile_ReadsAMultiBlockTemplate()
    {
        Directory.CreateDirectory(_directory);
        var twoBlocks = EdidFixtures.WithExtensionBlock(EdidFixtures.Pl2452UnitABytes());
        var path = Path.Combine(_directory, "twoblock.bin");
        File.WriteAllBytes(path, twoBlocks);

        var template = _store.LoadFromFile(path);

        Assert.NotNull(template);
        Assert.Equal(2, template!.Edid.BlockCount);
    }

    // ---------------------------------------------------------------- rejection

    /// <summary>
    /// The exact defect in the block originally proposed as the DM7000 default.
    /// </summary>
    [Fact]
    public void LoadFromFile_RejectsATemplateWithAnInvalidChecksum()
    {
        Directory.CreateDirectory(_directory);
        var broken = EdidFixtures.Pl2452UnitABytes();
        broken[EdidBlock.ChecksumOffset] ^= 0xFF;

        var path = Path.Combine(_directory, "broken.bin");
        File.WriteAllBytes(path, broken);

        Assert.Null(_store.LoadFromFile(path));
    }

    /// <summary>
    /// The literal block proposed for "DM7000_Native.bin" must be refused, not silently
    /// installed. Checked as a regression guard so it cannot be reintroduced.
    /// </summary>
    [Fact]
    public void LoadFromFile_RejectsTheOriginallyProposedDm7000Block()
    {
        const string proposed =
            "00FFFFFFFFFFFF000E72000070030000011D010380351E782AAC25A6554D99260F5054A54B00" +
            "714F8180A9C001010101010101010101023A801871382D40582C4500132B2100001E000000FD" +
            "00324B1E500F000A202020202020000000FC00444D373030300A202020202020000000FF0038" +
            "38300A2020202020202020003530";

        var bytes = Convert.FromHexString(proposed);

        // It parses as an EDID and even claims the right monitor name...
        Assert.True(EdidBlock.TryFromBytes(bytes, out var parsed));
        Assert.Equal("DM7000", parsed!.MonitorName);

        // ...but the checksum is wrong and the identity is a different manufacturer entirely.
        Assert.False(parsed.IsChecksumValid);
        Assert.NotEqual("CHR", parsed.ManufacturerCode);
        Assert.NotEqual(0x8910, parsed.ProductCode);

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "proposed.bin");
        File.WriteAllBytes(path, bytes);

        Assert.Null(_store.LoadFromFile(path));
    }

    [Fact]
    public void LoadFromFile_RejectsDataWithoutTheEdidHeader()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "notedid.bin");
        File.WriteAllBytes(path, new byte[EdidBlock.BlockSize]);

        Assert.Null(_store.LoadFromFile(path));
    }

    [Fact]
    public void LoadFromFile_RejectsAPartialBlock()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "short.bin");
        File.WriteAllBytes(path, EdidFixtures.Pl2452UnitABytes()[..100]);

        Assert.Null(_store.LoadFromFile(path));
    }

    [Fact]
    public void LoadFromFile_RejectsAHexFileWithNonHexCharacters()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "bad.hex");
        File.WriteAllText(path, "00FFFFFF NOT-HEX FFFFFF00");

        Assert.Null(_store.LoadFromFile(path));
    }

    [Fact]
    public void LoadFromFile_ReturnsNullForAMissingFile()
    {
        Assert.Null(_store.LoadFromFile(Path.Combine(_directory, "nope.bin")));
    }

    // ---------------------------------------------------------------- path safety

    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.bin")]
    [InlineData(@"C:\Windows\System32\evil.bin")]
    [InlineData("with:colon.bin")]
    public void LoadTemplate_RejectsNamesThatEscapeTheTemplateDirectory(string name)
    {
        Assert.Null(_store.LoadTemplate(name));
    }

    [Theory]
    [InlineData(@"..\escape.bin")]
    [InlineData(@"C:\rooted.bin")]
    public void SaveTemplate_RejectsNamesThatEscapeTheTemplateDirectory(string name)
    {
        Assert.Null(_store.SaveTemplate(name, EdidFixtures.Pl2452UnitABytes()));
    }

    // ---------------------------------------------------------------- saving

    [Fact]
    public void SaveTemplate_WritesAValidTemplateAndListsIt()
    {
        var path = _store.SaveTemplate("custom.bin", EdidFixtures.Pl2452UnitABytes());

        Assert.NotNull(path);
        Assert.True(File.Exists(path!));
        Assert.Contains(_store.ListTemplates(), t => t.Name == "custom.bin");
    }

    [Fact]
    public void SaveTemplate_RefusesAnInvalidEdid()
    {
        var broken = EdidFixtures.Pl2452UnitABytes();
        broken[EdidBlock.ChecksumOffset] ^= 0xFF;

        Assert.Null(_store.SaveTemplate("broken.bin", broken));
        Assert.False(File.Exists(Path.Combine(_directory, "broken.bin")));
    }

    [Fact]
    public void ListTemplates_SkipsInvalidFilesInsteadOfFailing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "good.bin"), EdidFixtures.Pl2452UnitABytes());
        File.WriteAllBytes(Path.Combine(_directory, "junk.bin"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(_directory, "readme.txt"), "not a template");

        var templates = _store.ListTemplates();

        Assert.Contains(templates, t => t.Name == "good.bin");
        Assert.DoesNotContain(templates, t => t.Name == "junk.bin");
        Assert.DoesNotContain(templates, t => t.Name == "readme.txt");
    }

    private static string InsertLineBreaks(string hex)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < hex.Length; i += 32)
            sb.AppendLine(hex.Substring(i, Math.Min(32, hex.Length - i)));

        return sb.ToString();
    }
}
