using Evolved.EdidManager;
using Evolved.EdidManager.Edid;
using Evolved.EdidManager.Pnp;
using Evolved.EdidManager.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace TouchMappingAgent.Tests.EdidTests;

/// <summary>
/// End-to-end tests for the orchestration, against a registry rooted in HKCU and a fake PnP
/// control. Covers the paths that decide whether the feature is safe to run on a production
/// machine: it must refuse without elevation, must never write an EDID with a bad checksum,
/// and must be idempotent so repeated runs do not drift the serials further each time.
/// </summary>
public class EdidManagerServiceTests : IDisposable
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

    private readonly RegistryKey _testRoot;
    private readonly string _testRootName;
    private readonly EdidRegistryStore _store;
    private readonly FakePnpControl _pnp = new();
    private readonly FakeElevation _elevation = new();
    private readonly EdidManagerService _service;

    public EdidManagerServiceTests()
    {
        _testRootName = $@"Software\PadaLumaTests\EdidSvc\{Guid.NewGuid():N}";
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

    private void SeedMonitor(string instanceId, byte[] edid)
    {
        using var key = _testRoot.CreateSubKey(
            $@"{EdidRegistryStore.EnumRootPath}\{instanceId}\Device Parameters", writable: true)!;
        key.SetValue(EdidRegistryStore.HardwareEdidValueName, edid, RegistryValueKind.Binary);
    }

    private void SeedCollidingPair()
    {
        SeedMonitor(EdidFixtures.Dm7000UnitAInstanceId, EdidFixtures.Dm7000Bytes());
        SeedMonitor(EdidFixtures.Dm7000UnitBInstanceId, EdidFixtures.Dm7000Bytes());
    }

    [Fact]
    public void ResolveCollisions_GivesEachCollidingMonitorAUniqueSerial()
    {
        SeedCollidingPair();

        var result = _service.ResolveCollisions();

        Assert.True(result.Success);
        Assert.Equal(2, result.Changed);

        var serials = _service.ReadAllMonitors().Select(m => m.Edid.SerialNumber).ToList();
        Assert.Equal(2, serials.Distinct().Count());
    }

    /// <summary>
    /// Every EDID written must satisfy the checksum rule. A driver rejects one that does not,
    /// and the usual symptom is a dark port — worse than the ambiguity being fixed.
    /// </summary>
    [Fact]
    public void ResolveCollisions_WritesOnlyChecksumValidEdids()
    {
        SeedCollidingPair();

        _service.ResolveCollisions();

        foreach (var instanceId in _store.EnumerateMonitorInstanceIds())
        {
            var written = _store.ReadOverride(instanceId);
            Assert.NotNull(written);

            var block = EdidBlock.FromBytes(written!);
            Assert.True(block.IsChecksumValid, $"Override for {instanceId} has an invalid checksum.");
        }
    }

    [Fact]
    public void ResolveCollisions_ReenumeratesEveryModifiedNode()
    {
        SeedCollidingPair();

        _service.ResolveCollisions();

        Assert.Equal(2, _pnp.Reenumerated.Count);
        Assert.Contains(EdidFixtures.Dm7000UnitAInstanceId, _pnp.Reenumerated);
        Assert.Contains(EdidFixtures.Dm7000UnitBInstanceId, _pnp.Reenumerated);
    }

    /// <summary>
    /// A failed re-enumeration is not a failed operation: the override is in the registry and
    /// takes effect at the next topology change. Reporting failure would invite an operator to
    /// re-run and re-write what is already correct.
    /// </summary>
    [Fact]
    public void ResolveCollisions_StillSucceeds_WhenReenumerationFails()
    {
        SeedCollidingPair();
        _pnp.Result = false;

        var result = _service.ResolveCollisions();

        Assert.True(result.Success);
        Assert.Equal(2, result.Changed);
    }

    /// <summary>
    /// Running twice must produce the same serials, not "880-A" then "880-A-A". The second run
    /// mutates the HARDWARE EDID again rather than the override it wrote the first time.
    /// </summary>
    [Fact]
    public void ResolveCollisions_IsIdempotent()
    {
        SeedCollidingPair();

        _service.ResolveCollisions();
        var afterFirst = _store.ReadOverride(EdidFixtures.Dm7000UnitAInstanceId);

        _service.ResolveCollisions();
        var afterSecond = _store.ReadOverride(EdidFixtures.Dm7000UnitAInstanceId);

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void ResolveCollisions_DoesNothingWhenSerialsAreAlreadyDistinct()
    {
        SeedMonitor(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitABytes());
        SeedMonitor(EdidFixtures.Pl2452UnitBInstanceId, EdidFixtures.Pl2452UnitBBytes());

        var result = _service.ResolveCollisions();

        Assert.True(result.Success);
        Assert.Equal(0, result.Changed);
        Assert.Empty(_pnp.Reenumerated);
        Assert.Null(_store.ReadOverride(EdidFixtures.Pl2452UnitAInstanceId));
    }

    [Fact]
    public void ResolveCollisions_RefusesWithoutElevation()
    {
        SeedCollidingPair();
        _elevation.IsElevated = false;

        var result = _service.ResolveCollisions();

        Assert.False(result.Success);
        Assert.Equal(EdidMessageKeys.ElevationRequired, result.Message.Key);
        Assert.Null(_store.ReadOverride(EdidFixtures.Dm7000UnitAInstanceId));
    }

    [Fact]
    public void ResolveCollisions_DryRunWritesNothing()
    {
        SeedCollidingPair();

        var result = _service.ResolveCollisions(dryRun: true);

        Assert.True(result.Success);
        Assert.Equal(0, result.Changed);
        Assert.Null(_store.ReadOverride(EdidFixtures.Dm7000UnitAInstanceId));
        Assert.Empty(_pnp.Reenumerated);
    }

    /// <summary>
    /// A dry run must be safe for an unprivileged operator to use for diagnosis.
    /// </summary>
    [Fact]
    public void ResolveCollisions_DryRunWorksWithoutElevation()
    {
        SeedCollidingPair();
        _elevation.IsElevated = false;

        var result = _service.ResolveCollisions(dryRun: true);

        Assert.True(result.Success);
    }

    [Fact]
    public void Analyze_ReportsCollisionsWithoutWriting()
    {
        SeedCollidingPair();

        var plan = _service.Analyze();

        Assert.Equal(2, plan.Count);
        Assert.Null(_store.ReadOverride(EdidFixtures.Dm7000UnitAInstanceId));
    }

    [Fact]
    public void RestoreDefaults_RemovesTheOverrideAndReenumerates()
    {
        SeedCollidingPair();
        _service.ResolveCollisions();
        _pnp.Reenumerated.Clear();

        var result = _service.RestoreDefaults(EdidFixtures.Dm7000UnitAInstanceId);

        Assert.True(result.Success);
        Assert.Null(_store.ReadOverride(EdidFixtures.Dm7000UnitAInstanceId));
        Assert.Contains(EdidFixtures.Dm7000UnitAInstanceId, _pnp.Reenumerated);

        // The hardware EDID must be intact — that is the whole point of "restore".
        var hardware = _store.ReadHardwareEdid(EdidFixtures.Dm7000UnitAInstanceId);
        Assert.Equal(880u, EdidBlock.FromBytes(hardware!).SerialNumber);
    }

    /// <summary>
    /// Operators have the interface path to hand from the display enumeration, not the instance
    /// id. Accepting both spares them a conversion they would otherwise get wrong.
    /// </summary>
    [Fact]
    public void RestoreDefaults_AcceptsADeviceInterfacePath()
    {
        SeedCollidingPair();
        _service.ResolveCollisions();

        var result = _service.RestoreDefaults(
            @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250116#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");

        Assert.True(result.Success);
        Assert.Null(_store.ReadOverride(EdidFixtures.Dm7000UnitAInstanceId));
    }

    [Fact]
    public void RestoreDefaults_RejectsAMalformedIdentifier()
    {
        var result = _service.RestoreDefaults("not-a-pnp-id");

        Assert.False(result.Success);
    }

    [Fact]
    public void RestoreDefaults_RefusesWithoutElevation()
    {
        SeedCollidingPair();
        _service.ResolveCollisions();
        _elevation.IsElevated = false;

        var result = _service.RestoreDefaults(EdidFixtures.Dm7000UnitAInstanceId);

        Assert.False(result.Success);
        Assert.NotNull(_store.ReadOverride(EdidFixtures.Dm7000UnitAInstanceId));
    }

    [Fact]
    public void RestoreAllDefaults_ClearsEveryOverride()
    {
        SeedCollidingPair();
        _service.ResolveCollisions();

        var result = _service.RestoreAllDefaults();

        Assert.True(result.Success);
        Assert.Equal(2, result.Changed);
        Assert.All(_store.EnumerateMonitorInstanceIds(),
            id => Assert.Null(_store.ReadOverride(id)));
    }

    /// <summary>
    /// A panel whose stored EDID is corrupt must be skipped, not propagated: writing an
    /// override derived from a bad checksum would make a marginal panel permanently unusable.
    /// </summary>
    [Fact]
    public void ReadAllMonitors_SkipsEdidsWithAnInvalidChecksum()
    {
        var corrupt = EdidFixtures.Dm7000Bytes();
        corrupt[EdidBlock.ChecksumOffset] ^= 0xFF;

        SeedMonitor(EdidFixtures.Dm7000UnitAInstanceId, corrupt);
        SeedMonitor(EdidFixtures.Dm7000UnitBInstanceId, EdidFixtures.Dm7000Bytes());

        var monitors = _service.ReadAllMonitors();

        Assert.Single(monitors);
        Assert.Equal(EdidFixtures.Dm7000UnitBInstanceId, monitors[0].PnpInstanceId);
    }

    [Fact]
    public void ReadAllMonitors_PrefersAnInstalledOverride()
    {
        SeedCollidingPair();
        _service.ResolveCollisions();

        var monitors = _service.ReadAllMonitors();

        Assert.All(monitors, m => Assert.True(m.IsOverridden));
        Assert.Equal(2, monitors.Select(m => m.Edid.SerialNumber).Distinct().Count());
    }
}
