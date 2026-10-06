using Evolved.EdidManager;
using Evolved.EdidManager.Edid;
using Evolved.EdidManager.Pnp;
using Evolved.EdidManager.Registry;
using Evolved.EdidManager.Templates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace TouchMappingAgent.Tests.EdidTests;

/// <summary>
/// Tests for template injection — the fallback for a range extender that passes no usable DDC
/// through.
///
/// Distinct from the collision path in one important way: there may be NO hardware EDID at all
/// on the target port, so the code cannot read anything to clone. It must still refuse a bad
/// template, because at that point nothing else stands between a broken block and a dark port.
/// </summary>
public class ApplyCustomTemplateTests : IDisposable
{
    private sealed class FakePnpControl : IPnpDeviceControl
    {
        public List<string> Reenumerated { get; } = new();
        public bool Result { get; set; } = true;

        public bool ReenumerateDevice(string instanceId)
        {
            Reenumerated.Add(instanceId);
            return Result;
        }
    }

    private sealed class FakeElevation : IElevationCheck
    {
        public bool IsElevated { get; set; } = true;
    }

    private const string ExtenderPort = @"DISPLAY\CHR8910\5&2c72b841&0&UID250116";

    private readonly RegistryKey _testRoot;
    private readonly string _testRootName;
    private readonly EdidRegistryStore _store;
    private readonly FakePnpControl _pnp = new();
    private readonly FakeElevation _elevation = new();
    private readonly EdidManagerService _service;

    public ApplyCustomTemplateTests()
    {
        _testRootName = $@"Software\PadaLumaTests\EdidTpl\{Guid.NewGuid():N}";
        _testRoot = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_testRootName, writable: true)!;
        _store = new EdidRegistryStore(NullLogger<EdidRegistryStore>.Instance, _testRoot);
        _service = new EdidManagerService(
            NullLogger<EdidManagerService>.Instance, _store, _pnp, _elevation);
    }

    public void Dispose()
    {
        _testRoot.Dispose();
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_testRootName, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best effort.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Creates the Device Parameters key WITHOUT an EDID value — the extender case.</summary>
    private void SeedPortWithoutEdid(string instanceId)
    {
        using var _ = _testRoot.CreateSubKey(
            $@"{EdidRegistryStore.EnumRootPath}\{instanceId}\Device Parameters", writable: true);
    }

    private static byte[] Dm7000Template() => EdidTemplateStore.BuiltInDm7000.Edid.ToArray();

    // ---------------------------------------------------------------- the extender case

    [Fact]
    public void ApplyCustomTemplate_WorksOnAPortThatReportsNoEdidAtAll()
    {
        SeedPortWithoutEdid(ExtenderPort);
        Assert.Null(_store.ReadHardwareEdid(ExtenderPort));

        var result = _service.ApplyCustomTemplate(ExtenderPort, Dm7000Template(), "880-A");

        Assert.True(result.Success);
        Assert.Equal(1, result.Changed);

        var installed = _store.ReadOverride(ExtenderPort);
        Assert.NotNull(installed);

        var edid = EdidBlock.FromBytes(installed!);
        Assert.Equal("CHR", edid.ManufacturerCode);
        Assert.Equal(0x8910, edid.ProductCode);
        Assert.True(edid.IsChecksumValid);
    }

    /// <summary>
    /// The DM7000 has no 0xFF descriptor but does have an empty slot; stamping the serial text
    /// into it is what makes two otherwise identical templates tellable apart.
    /// </summary>
    [Fact]
    public void ApplyCustomTemplate_StampsTheSerialTextIntoAnEmptyDescriptorSlot()
    {
        SeedPortWithoutEdid(ExtenderPort);

        _service.ApplyCustomTemplate(ExtenderPort, Dm7000Template(), "880-A");

        var edid = EdidBlock.FromBytes(_store.ReadOverride(ExtenderPort)!);
        Assert.Equal("880-A", edid.SerialText);
        Assert.True(edid.IsChecksumValid);
    }

    /// <summary>
    /// Two extender ports given the same template must end up distinguishable — that is the
    /// entire purpose of the custom serial.
    /// </summary>
    [Fact]
    public void ApplyCustomTemplate_MakesTwoPortsOnTheSameTemplateDistinguishable()
    {
        const string portB = @"DISPLAY\CHR8910\5&2c72b841&0&UID250118";
        SeedPortWithoutEdid(ExtenderPort);
        SeedPortWithoutEdid(portB);

        _service.ApplyCustomTemplate(ExtenderPort, Dm7000Template(), "880-A");
        _service.ApplyCustomTemplate(portB, Dm7000Template(), "880-B");

        var a = EdidBlock.FromBytes(_store.ReadOverride(ExtenderPort)!);
        var b = EdidBlock.FromBytes(_store.ReadOverride(portB)!);

        Assert.NotEqual(a.IdentityKey, b.IdentityKey);
        Assert.NotEqual(a.SerialNumber, b.SerialNumber);
        Assert.Equal("880-A", a.SerialText);
        Assert.Equal("880-B", b.SerialText);
    }

    [Fact]
    public void ApplyCustomTemplate_OverwritesAnExistingHardwareEdid()
    {
        // A port that DOES report an EDID — the "manual override" case.
        using (var key = _testRoot.CreateSubKey(
            $@"{EdidRegistryStore.EnumRootPath}\{ExtenderPort}\Device Parameters", writable: true)!)
        {
            key.SetValue(EdidRegistryStore.HardwareEdidValueName,
                EdidFixtures.Pl2452UnitABytes(), RegistryValueKind.Binary);
        }

        var result = _service.ApplyCustomTemplate(ExtenderPort, Dm7000Template(), "1001");

        Assert.True(result.Success);

        var edid = EdidBlock.FromBytes(_store.ReadOverride(ExtenderPort)!);
        Assert.Equal("CHR", edid.ManufacturerCode);       // the template won
        Assert.Equal(1001u, edid.SerialNumber);

        // The hardware EDID underneath must be untouched, so RestoreDefaults can go back.
        Assert.Equal(EdidFixtures.Pl2452UnitABytes(), _store.ReadHardwareEdid(ExtenderPort));
    }

    [Fact]
    public void ApplyCustomTemplate_PreservesExtensionBlocks()
    {
        SeedPortWithoutEdid(ExtenderPort);
        var twoBlocks = EdidFixtures.WithExtensionBlock(EdidFixtures.Pl2452UnitABytes());

        _service.ApplyCustomTemplate(ExtenderPort, twoBlocks, "1001");

        var installed = _store.ReadOverride(ExtenderPort)!;
        Assert.Equal(twoBlocks.Length, installed.Length);

        for (int i = EdidBlock.BlockSize; i < twoBlocks.Length; i++)
            Assert.True(twoBlocks[i] == installed[i], $"Extension byte {i} changed.");
    }

    // ---------------------------------------------------------------- rejection

    /// <summary>
    /// The decisive guard: a template with a bad checksum must never reach the registry. On an
    /// extender port there is no hardware EDID to fall back to, so installing a block the
    /// driver rejects leaves nothing at all.
    /// </summary>
    [Fact]
    public void ApplyCustomTemplate_RefusesATemplateWithAnInvalidChecksum()
    {
        SeedPortWithoutEdid(ExtenderPort);
        var broken = Dm7000Template();
        broken[EdidBlock.ChecksumOffset] ^= 0xFF;

        var result = _service.ApplyCustomTemplate(ExtenderPort, broken, "880-A");

        Assert.False(result.Success);
        Assert.Equal(EdidMessageKeys.TemplateBadChecksum, result.Message.Key);
        Assert.Null(_store.ReadOverride(ExtenderPort));
        Assert.Empty(_pnp.Reenumerated);
    }

    [Fact]
    public void ApplyCustomTemplate_RefusesTheOriginallyProposedDm7000Block()
    {
        SeedPortWithoutEdid(ExtenderPort);

        var proposed = Convert.FromHexString(
            "00FFFFFFFFFFFF000E72000070030000011D010380351E782AAC25A6554D99260F5054A54B00" +
            "714F8180A9C001010101010101010101023A801871382D40582C4500132B2100001E000000FD" +
            "00324B1E500F000A202020202020000000FC00444D373030300A202020202020000000FF0038" +
            "38300A2020202020202020003530");

        var result = _service.ApplyCustomTemplate(ExtenderPort, proposed, "880-A");

        Assert.False(result.Success);
        Assert.Null(_store.ReadOverride(ExtenderPort));
    }

    [Fact]
    public void ApplyCustomTemplate_RefusesDataThatIsNotAnEdid()
    {
        SeedPortWithoutEdid(ExtenderPort);

        var result = _service.ApplyCustomTemplate(ExtenderPort, new byte[EdidBlock.BlockSize], "1");

        Assert.False(result.Success);
        Assert.Null(_store.ReadOverride(ExtenderPort));
    }

    [Fact]
    public void ApplyCustomTemplate_RefusesAPartialBlock()
    {
        SeedPortWithoutEdid(ExtenderPort);

        var result = _service.ApplyCustomTemplate(ExtenderPort, Dm7000Template()[..100], "1");

        Assert.False(result.Success);
    }

    [Fact]
    public void ApplyCustomTemplate_RefusesAMalformedPnpId()
    {
        var result = _service.ApplyCustomTemplate("not-a-pnp-id", Dm7000Template(), "1");

        Assert.False(result.Success);
    }

    [Fact]
    public void ApplyCustomTemplate_RefusesWithoutElevation()
    {
        SeedPortWithoutEdid(ExtenderPort);
        _elevation.IsElevated = false;

        var result = _service.ApplyCustomTemplate(ExtenderPort, Dm7000Template(), "880-A");

        Assert.False(result.Success);
        Assert.Equal(EdidMessageKeys.ElevationRequired, result.Message.Key);
        Assert.Null(_store.ReadOverride(ExtenderPort));
    }

    /// <summary>
    /// Template validation runs BEFORE the elevation check, so an operator can discover a
    /// broken file without first having to elevate.
    /// </summary>
    [Fact]
    public void ApplyCustomTemplate_ReportsABrokenTemplateEvenWithoutElevation()
    {
        SeedPortWithoutEdid(ExtenderPort);
        _elevation.IsElevated = false;

        var broken = Dm7000Template();
        broken[EdidBlock.ChecksumOffset] ^= 0xFF;

        var result = _service.ApplyCustomTemplate(ExtenderPort, broken, "880-A");

        Assert.False(result.Success);
        Assert.Equal(EdidMessageKeys.TemplateBadChecksum, result.Message.Key);
    }

    [Fact]
    public void ApplyCustomTemplate_AcceptsADeviceInterfacePath()
    {
        SeedPortWithoutEdid(ExtenderPort);

        var result = _service.ApplyCustomTemplate(
            @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250116#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            Dm7000Template(), "880-A");

        Assert.True(result.Success);
        Assert.NotNull(_store.ReadOverride(ExtenderPort));
    }

    [Fact]
    public void RestoreDefaults_RemovesAnAppliedTemplate()
    {
        SeedPortWithoutEdid(ExtenderPort);
        _service.ApplyCustomTemplate(ExtenderPort, Dm7000Template(), "880-A");

        var result = _service.RestoreDefaults(ExtenderPort);

        Assert.True(result.Success);
        Assert.Null(_store.ReadOverride(ExtenderPort));
    }

    // ---------------------------------------------------------------- serial derivation

    [Theory]
    [InlineData("1001", 1001u)]
    [InlineData("880", 880u)]
    [InlineData("4294967295", 4294967295u)]
    public void DeriveSerial_UsesAPurelyNumericValueDirectly(string input, uint expected)
    {
        var (serial, text) = EdidManagerService.DeriveSerial(input, EdidTemplateStore.BuiltInDm7000.Edid);

        Assert.Equal(expected, serial);
        Assert.Equal(input, text);
    }

    /// <summary>
    /// "880-A" cannot be a numeric serial, so a value is derived from it. Derivation must be
    /// stable: the same string has to yield the same number on every run, or a re-application
    /// would silently change the serial an operator already recorded.
    /// </summary>
    [Fact]
    public void DeriveSerial_IsStableForNonNumericInput()
    {
        var first = EdidManagerService.DeriveSerial("880-A", EdidTemplateStore.BuiltInDm7000.Edid);
        var second = EdidManagerService.DeriveSerial("880-A", EdidTemplateStore.BuiltInDm7000.Edid);

        Assert.Equal(first, second);
        Assert.Equal("880-A", first.SerialText);
    }

    [Fact]
    public void DeriveSerial_ProducesDifferentNumbersForDifferentText()
    {
        var a = EdidManagerService.DeriveSerial("880-A", EdidTemplateStore.BuiltInDm7000.Edid);
        var b = EdidManagerService.DeriveSerial("880-B", EdidTemplateStore.BuiltInDm7000.Edid);

        Assert.NotEqual(a.SerialNumber, b.SerialNumber);
    }

    [Fact]
    public void DeriveSerial_FallsBackToTheTemplateSerialWhenNoneIsGiven()
    {
        var (serial, _) = EdidManagerService.DeriveSerial("", EdidTemplateStore.BuiltInDm7000.Edid);

        Assert.Equal(880u, serial);
    }
}
