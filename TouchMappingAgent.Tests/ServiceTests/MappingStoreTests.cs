using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using TouchMappingAgent.Service.Services;
using TouchMappingAgent.Shared.Models;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Tests for hardware-anchored persistence.
///
/// Runs against HKCU rather than the production HKLM root: writing HKLM needs elevation the
/// test runner does not have. The key layout, escaping and round-trip semantics exercised are
/// identical — only the hive differs.
/// </summary>
public class MappingStoreTests : IDisposable
{
    private const string TouchParentA = @"USB\VID_14E1&PID_3508\7&1b9afb93&0&6";
    private const string TouchParentB = @"USB\VID_14E1&PID_3508\7&235bf858&0&6";

    private readonly RegistryKey _testRoot;
    private readonly string _testRootName;
    private readonly MappingStore _store;

    public MappingStoreTests()
    {
        // Each test class instance gets its own subtree so parallel runs cannot collide.
        _testRootName = $@"Software\PadaLumaTests\{Guid.NewGuid():N}";
        _testRoot = Registry.CurrentUser.CreateSubKey(_testRootName, writable: true)!;
        _store = new MappingStore(NullLogger<MappingStore>.Instance, _testRoot);
    }

    public void Dispose()
    {
        _testRoot.Dispose();
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_testRootName, throwOnMissingSubKey: false);
        }
        catch
        {
            // Cleanup is best effort; a leftover test key is harmless.
        }

        GC.SuppressFinalize(this);
    }

    private static TouchMapping SampleMapping(string touchKey = TouchParentA) => new(
        TouchHardwareKey: HidDeviceInfo.NormalizeInstanceId(touchKey),
        MonitorHardwareKey: @"\\?\DISPLAY#CHR8910#5&2C72B841&0&UID250118",
        MonitorConnectorLabel: "DP-3",
        MonitorTargetId: 250118,
        MonitorBoundsKey: "1920,0,1920,1080",
        TouchProductName: "PM1715",
        MonitorFriendlyName: "DM7000",
        LastKnownTouchDevicePath: @"\\?\HID#VID_14E1&PID_3508#8&19abaff7&0&0000#{guid}",
        LastKnownMonitorDeviceId: @"\\.\DISPLAY3");

    [Fact]
    public void Save_ThenGetAll_RoundTripsEveryAnchor()
    {
        var original = SampleMapping();

        Assert.True(_store.Save(original));

        var loaded = Assert.Single(_store.GetAll());

        Assert.Equal(original.TouchHardwareKey, loaded.TouchHardwareKey);
        Assert.Equal(original.MonitorHardwareKey, loaded.MonitorHardwareKey);
        Assert.Equal(original.MonitorConnectorLabel, loaded.MonitorConnectorLabel);
        Assert.Equal(original.MonitorTargetId, loaded.MonitorTargetId);
        Assert.Equal(original.MonitorBoundsKey, loaded.MonitorBoundsKey);
        Assert.Equal(original.TouchProductName, loaded.TouchProductName);
        Assert.Equal(original.MonitorFriendlyName, loaded.MonitorFriendlyName);
    }

    /// <summary>
    /// A PnP instance id contains backslashes, which are subkey separators. Without escaping,
    /// one mapping would silently become a three-level nested tree and never read back.
    /// </summary>
    [Fact]
    public void Save_EscapesBackslashesSoOneMappingIsOneSubkey()
    {
        _store.Save(SampleMapping());

        using var mappingsKey = _testRoot.OpenSubKey(MappingStore.MappingKeyPath);
        var subKeys = mappingsKey!.GetSubKeyNames();

        Assert.Single(subKeys);
        Assert.DoesNotContain('\\', subKeys[0]);
        Assert.Equal(
            HidDeviceInfo.NormalizeInstanceId(TouchParentA),
            MappingStore.UnescapeKeyName(subKeys[0]));
    }

    [Fact]
    public void Save_TwiceForSameTouchDevice_ReplacesInsteadOfDuplicating()
    {
        _store.Save(SampleMapping());
        _store.Save(SampleMapping() with { MonitorConnectorLabel = "DP-2", MonitorTargetId = 250116 });

        var loaded = Assert.Single(_store.GetAll());
        Assert.Equal("DP-2", loaded.MonitorConnectorLabel);
        Assert.Equal(250116u, loaded.MonitorTargetId);
    }

    [Fact]
    public void Save_KeepsSeparateEntriesForDifferentDigitizers()
    {
        _store.Save(SampleMapping(TouchParentA));
        _store.Save(SampleMapping(TouchParentB) with { MonitorConnectorLabel = "DP-2" });

        var all = _store.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.MonitorConnectorLabel == "DP-3");
        Assert.Contains(all, m => m.MonitorConnectorLabel == "DP-2");
    }

    [Fact]
    public void Save_RefusesMappingWithoutTouchAnchor()
    {
        var anchorless = SampleMapping() with { TouchHardwareKey = "" };

        Assert.False(_store.Save(anchorless));
        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public void Delete_RemovesOnlyTheNamedMapping()
    {
        _store.Save(SampleMapping(TouchParentA));
        _store.Save(SampleMapping(TouchParentB));

        Assert.True(_store.Delete(TouchParentA));

        var remaining = Assert.Single(_store.GetAll());
        Assert.Equal(HidDeviceInfo.NormalizeInstanceId(TouchParentB), remaining.TouchHardwareKey);
    }

    [Fact]
    public void Delete_ReturnsFalseForUnknownMapping()
    {
        Assert.False(_store.Delete(@"USB\VID_DEAD&PID_BEEF\1&2&3"));
    }

    [Fact]
    public void MarkApplied_StampsTheTimestamp()
    {
        _store.Save(SampleMapping());
        var appliedAt = new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Utc);

        _store.MarkApplied(TouchParentA, appliedAt);

        var loaded = Assert.Single(_store.GetAll());
        Assert.Equal(appliedAt.ToString("O"), loaded.LastAppliedUtc);
    }

    /// <summary>
    /// Phase-1 records were flat values (value name = HID interface path, data =
    /// "\\.\DISPLAYn"). They cannot be upgraded — the anchors were never captured — so they
    /// are removed, turning an invisible stale record into a visible "must be re-learned".
    /// </summary>
    [Fact]
    public void Save_RemovesLegacyFlatValues()
    {
        using (var legacy = _testRoot.CreateSubKey(MappingStore.MappingKeyPath, writable: true)!)
        {
            legacy.SetValue(
                @"\\?\HID#VID_14E1&PID_3508#8&25188e6b&0&0000#{guid}",
                @"\\.\DISPLAY2",
                RegistryValueKind.String);
        }

        _store.Save(SampleMapping());

        using var mappingsKey = _testRoot.OpenSubKey(MappingStore.MappingKeyPath);
        Assert.Empty(mappingsKey!.GetValueNames().Where(n => n.Length > 0));
        Assert.Single(_store.GetAll());
    }

    [Fact]
    public void GetAll_SkipsEntriesWithNoMonitorAnchor()
    {
        _store.Save(SampleMapping());

        // Corrupt the entry by stripping every monitor anchor.
        using (var mappingsKey = _testRoot.OpenSubKey(MappingStore.MappingKeyPath, writable: true)!)
        using (var entry = mappingsKey.OpenSubKey(mappingsKey.GetSubKeyNames()[0], writable: true)!)
        {
            entry.SetValue(TouchMapping.ValueNames.MonitorHardwareKey, "");
            entry.SetValue(TouchMapping.ValueNames.MonitorConnectorLabel, "");
            entry.SetValue(TouchMapping.ValueNames.MonitorBoundsKey, "");
        }

        Assert.Empty(_store.GetAll());
    }

    [Fact]
    public void GetAll_OnMissingKey_ReturnsEmptyRatherThanThrowing()
    {
        Assert.Empty(_store.GetAll());
    }
}
