using Evolved.EdidManager.Edid;
using Evolved.EdidManager.Pnp;
using Evolved.EdidManager.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace TouchMappingAgent.Tests.EdidTests;

/// <summary>
/// Tests for PnP id conversion and the registry override layout.
///
/// The store runs against HKCU rather than the production HKLM root: writing under
/// SYSTEM\CurrentControlSet\Enum needs elevation the test runner does not have. The key
/// structure exercised is identical — only the hive differs.
/// </summary>
public class EdidRegistryStoreTests : IDisposable
{
    private readonly RegistryKey _testRoot;
    private readonly string _testRootName;
    private readonly EdidRegistryStore _store;

    public EdidRegistryStoreTests()
    {
        _testRootName = $@"Software\PadaLumaTests\Edid\{Guid.NewGuid():N}";
        _testRoot = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(_testRootName, writable: true)!;
        _store = new EdidRegistryStore(NullLogger<EdidRegistryStore>.Instance, _testRoot);
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

    private void SeedMonitor(string instanceId, byte[] edid)
    {
        var path = $@"{EdidRegistryStore.EnumRootPath}\{instanceId}\Device Parameters";
        using var key = _testRoot.CreateSubKey(path, writable: true)!;
        key.SetValue(EdidRegistryStore.HardwareEdidValueName, edid, RegistryValueKind.Binary);
    }

    // ---------------------------------------------------------------- PnP ids

    [Fact]
    public void FromDevicePath_ConvertsAnInterfacePathToAnInstanceId()
    {
        const string devicePath =
            @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250116#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        Assert.Equal(
            @"DISPLAY\CHR8910\5&2c72b841&0&UID250116",
            PnpDeviceInstanceId.FromDevicePath(devicePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"DISPLAY\CHR8910\5&2c72b841&0&UID250116")]   // already an instance id
    public void FromDevicePath_ReturnsNullForNonInterfacePaths(string? input)
    {
        Assert.Null(PnpDeviceInstanceId.FromDevicePath(input));
    }

    [Theory]
    [InlineData(@"DISPLAY\CHR8910\5&2c72b841&0&UID250116", true)]
    [InlineData(@"DISPLAY\IVM610A\8&abf64df&0&UID256", true)]
    [InlineData(@"DISPLAY\CHR8910", false)]                       // too few segments
    [InlineData(@"\\?\DISPLAY#CHR8910#5&x", false)]               // interface path, not instance id
    [InlineData(@"DISPLAY\..\..\Services", false)]                // path traversal
    [InlineData("", false)]
    public void IsValidInstanceId_AcceptsOnlyWellFormedIds(string instanceId, bool expected)
    {
        Assert.Equal(expected, PnpDeviceInstanceId.IsValidInstanceId(instanceId));
    }

    // ---------------------------------------------------------------- store

    [Fact]
    public void ReadHardwareEdid_ReturnsTheSeededBytes()
    {
        var edid = EdidFixtures.Pl2452UnitABytes();
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, edid);

        Assert.Equal(edid, _store.ReadHardwareEdid(EdidFixtures.Pl2452UnitAInstanceId));
    }

    [Fact]
    public void ReadHardwareEdid_ReturnsNullForAnUnknownMonitor()
    {
        Assert.Null(_store.ReadHardwareEdid(@"DISPLAY\NOSUCH\1&2&3"));
    }

    /// <summary>
    /// The layout Windows actually reads: a SUBKEY named EDID_OVERRIDE whose values are the
    /// block index. A flat REG_BINARY value of that name next to EDID is read by nothing — the
    /// write would succeed and the override would never take effect.
    /// </summary>
    [Fact]
    public void WriteOverride_CreatesTheDocumentedSubkeyLayout()
    {
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());
        var edid = EdidFixtures.Pl2452UnitABytes();

        Assert.True(_store.WriteOverride(EdidFixtures.Pl2452UnitAInstanceId, edid));

        var overridePath =
            $@"{EdidRegistryStore.EnumRootPath}\{EdidFixtures.Pl2452UnitAInstanceId}" +
            $@"\Device Parameters\{EdidRegistryStore.OverrideKeyName}";

        using var overrideKey = _testRoot.OpenSubKey(overridePath);
        Assert.NotNull(overrideKey);

        Assert.Equal(new[] { "0" }, overrideKey!.GetValueNames());
        Assert.Equal(RegistryValueKind.Binary, overrideKey.GetValueKind("0"));
        Assert.Equal(edid, (byte[])overrideKey.GetValue("0")!);

        // And it must NOT be a flat value alongside EDID.
        using var deviceParameters = _testRoot.OpenSubKey(
            $@"{EdidRegistryStore.EnumRootPath}\{EdidFixtures.Pl2452UnitAInstanceId}\Device Parameters");
        Assert.DoesNotContain(EdidRegistryStore.OverrideKeyName, deviceParameters!.GetValueNames());
    }

    [Fact]
    public void WriteOverride_SplitsMultiBlockEdidIntoNumberedValues()
    {
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());
        var twoBlocks = EdidFixtures.WithExtensionBlock(EdidFixtures.Pl2452UnitABytes());

        Assert.True(_store.WriteOverride(EdidFixtures.Pl2452UnitAInstanceId, twoBlocks));

        var overridePath =
            $@"{EdidRegistryStore.EnumRootPath}\{EdidFixtures.Pl2452UnitAInstanceId}" +
            $@"\Device Parameters\{EdidRegistryStore.OverrideKeyName}";

        using var overrideKey = _testRoot.OpenSubKey(overridePath);

        Assert.Equal(new[] { "0", "1" }, overrideKey!.GetValueNames().OrderBy(n => n).ToArray());
        Assert.Equal(twoBlocks[..EdidBlock.BlockSize], (byte[])overrideKey.GetValue("0")!);
        Assert.Equal(twoBlocks[EdidBlock.BlockSize..], (byte[])overrideKey.GetValue("1")!);
    }

    [Fact]
    public void ReadOverride_ReassemblesTheBlocksInOrder()
    {
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());
        var twoBlocks = EdidFixtures.WithExtensionBlock(EdidFixtures.Pl2452UnitABytes());

        _store.WriteOverride(EdidFixtures.Pl2452UnitAInstanceId, twoBlocks);

        Assert.Equal(twoBlocks, _store.ReadOverride(EdidFixtures.Pl2452UnitAInstanceId));
    }

    /// <summary>
    /// Overwriting a two-block override with a one-block one must not leave block "1" behind:
    /// the reader concatenates every numbered value it finds, so a leftover would silently be
    /// appended to the new EDID.
    /// </summary>
    [Fact]
    public void WriteOverride_DiscardsLeftoverBlocksFromAPreviousOverride()
    {
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());

        _store.WriteOverride(
            EdidFixtures.Pl2452UnitAInstanceId,
            EdidFixtures.WithExtensionBlock(EdidFixtures.Pl2452UnitABytes()));

        _store.WriteOverride(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());

        var reread = _store.ReadOverride(EdidFixtures.Pl2452UnitAInstanceId);

        Assert.Equal(EdidBlock.BlockSize, reread!.Length);
    }

    [Fact]
    public void WriteOverride_RejectsDataThatIsNotAWholeNumberOfBlocks()
    {
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());

        Assert.False(_store.WriteOverride(EdidFixtures.Pl2452UnitAInstanceId, new byte[100]));
        Assert.False(_store.WriteOverride(EdidFixtures.Pl2452UnitAInstanceId, Array.Empty<byte>()));
    }

    [Fact]
    public void WriteOverride_RejectsAMalformedInstanceId()
    {
        Assert.False(_store.WriteOverride(@"DISPLAY\..\Services", EdidFixtures.Pl2452UnitABytes()));
    }

    [Fact]
    public void RemoveOverride_DeletesTheKeyAndRestoresTheHardwareView()
    {
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());
        _store.WriteOverride(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());

        Assert.True(_store.RemoveOverride(EdidFixtures.Pl2452UnitAInstanceId));
        Assert.Null(_store.ReadOverride(EdidFixtures.Pl2452UnitAInstanceId));

        // The hardware EDID must survive untouched — that is what "restore defaults" means.
        Assert.Equal(
            EdidFixtures.Pl2452UnitABytes(),
            _store.ReadHardwareEdid(EdidFixtures.Pl2452UnitAInstanceId));
    }

    [Fact]
    public void RemoveOverride_ReturnsFalseWhenNoneIsInstalled()
    {
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());

        Assert.False(_store.RemoveOverride(EdidFixtures.Pl2452UnitAInstanceId));
    }

    [Fact]
    public void EnumerateMonitorInstanceIds_ReturnsOnlyMonitorsThatReportAnEdid()
    {
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());
        SeedMonitor(EdidFixtures.Pl2452UnitBInstanceId, EdidFixtures.Pl2452UnitBBytes());

        // A node without an EDID, like the "Default_Monitor" entries Windows keeps for past
        // connections — there is nothing to de-collide there.
        using (var _ = _testRoot.CreateSubKey(
            $@"{EdidRegistryStore.EnumRootPath}\DISPLAY\Default_Monitor\1&2&3\Device Parameters",
            writable: true))
        {
        }

        var ids = _store.EnumerateMonitorInstanceIds();

        Assert.Equal(2, ids.Count);
        Assert.Contains(EdidFixtures.Pl2452UnitAInstanceId, ids);
        Assert.Contains(EdidFixtures.Pl2452UnitBInstanceId, ids);
    }
}
