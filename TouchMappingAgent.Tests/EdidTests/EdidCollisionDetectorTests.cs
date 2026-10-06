using Evolved.EdidManager.Edid;
using Xunit;

namespace TouchMappingAgent.Tests.EdidTests;

/// <summary>
/// Tests for deciding which monitors actually need a new serial.
///
/// The two failure modes that matter pull in opposite directions: missing a real collision
/// leaves the installation with indistinguishable displays, while flagging a false one rewrites
/// the EDID of a panel that was fine. The second is the more damaging, because it modifies
/// firmware data on hardware that had no problem.
/// </summary>
public class EdidCollisionDetectorTests
{
    private static MonitorEdid Dm7000(string instanceId) =>
        new(instanceId, EdidFixtures.Dm7000());

    [Fact]
    public void BuildPlan_DetectsIdenticalPanels()
    {
        var monitors = new[]
        {
            Dm7000(EdidFixtures.Dm7000UnitAInstanceId),
            Dm7000(EdidFixtures.Dm7000UnitBInstanceId)
        };

        var plan = EdidCollisionDetector.BuildPlan(monitors);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, entry => Assert.Equal(2, entry.GroupSize));
    }

    /// <summary>
    /// THE false-positive guard, taken straight from the development machine: two iiyama PL2452
    /// share the numeric serial 0x01010101 — a "not programmed" placeholder — but carry
    /// distinct 0xFF texts. They are distinguishable and must be left alone.
    /// </summary>
    [Fact]
    public void BuildPlan_IgnoresPanelsThatDifferInSerialTextOnly()
    {
        var monitors = new[]
        {
            new MonitorEdid(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitA()),
            new MonitorEdid(EdidFixtures.Pl2452UnitBInstanceId, EdidFixtures.Pl2452UnitB())
        };

        Assert.Empty(EdidCollisionDetector.BuildPlan(monitors));
    }

    /// <summary>
    /// The same pair must still be REPORTED, so an operator investigating a tool that reads
    /// only the numeric serial can see why it sees two identical displays.
    /// </summary>
    [Fact]
    public void FindNumericOnlyCollisions_ReportsThePlaceholderSerialPair()
    {
        var monitors = new[]
        {
            new MonitorEdid(EdidFixtures.Pl2452UnitAInstanceId, EdidFixtures.Pl2452UnitA()),
            new MonitorEdid(EdidFixtures.Pl2452UnitBInstanceId, EdidFixtures.Pl2452UnitB())
        };

        var groups = EdidCollisionDetector.FindNumericOnlyCollisions(monitors);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Count);
        Assert.Equal(0x01010101u, group[0].Edid.SerialNumber);
    }

    /// <summary>
    /// A fully identical pair belongs to BuildPlan, not to the numeric-only diagnostic —
    /// otherwise it would be both rewritten and reported as "left alone".
    /// </summary>
    [Fact]
    public void FindNumericOnlyCollisions_ExcludesFullyIdenticalPanels()
    {
        var monitors = new[]
        {
            Dm7000(EdidFixtures.Dm7000UnitAInstanceId),
            Dm7000(EdidFixtures.Dm7000UnitBInstanceId)
        };

        Assert.Empty(EdidCollisionDetector.FindNumericOnlyCollisions(monitors));
    }

    [Fact]
    public void BuildPlan_AssignsDistinctSerialsWithinAGroup()
    {
        var monitors = new[]
        {
            Dm7000(EdidFixtures.Dm7000UnitAInstanceId),
            Dm7000(EdidFixtures.Dm7000UnitBInstanceId)
        };

        var plan = EdidCollisionDetector.BuildPlan(monitors);
        var serials = plan.Select(e => e.NewSerialNumber).ToList();
        var texts = plan.Select(e => e.NewSerialText).ToList();

        Assert.Equal(serials.Count, serials.Distinct().Count());
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    /// <summary>
    /// The generated serial must not depend on the order monitors happened to be enumerated in,
    /// or the same panel would get a different serial on the next boot and every consumer that
    /// remembered the old one would break.
    /// </summary>
    [Fact]
    public void BuildPlan_IsDeterministicRegardlessOfEnumerationOrder()
    {
        var a = Dm7000(EdidFixtures.Dm7000UnitAInstanceId);
        var b = Dm7000(EdidFixtures.Dm7000UnitBInstanceId);

        var forward = EdidCollisionDetector.BuildPlan(new[] { a, b });
        var reversed = EdidCollisionDetector.BuildPlan(new[] { b, a });

        var forwardMap = forward.ToDictionary(e => e.Monitor.PnpInstanceId, e => e.NewSerialNumber);
        var reversedMap = reversed.ToDictionary(e => e.Monitor.PnpInstanceId, e => e.NewSerialNumber);

        Assert.Equal(forwardMap, reversedMap);
    }

    [Fact]
    public void BuildPlan_LeavesASinglePanelAlone()
    {
        var monitors = new[] { Dm7000(EdidFixtures.Dm7000UnitAInstanceId) };

        Assert.Empty(EdidCollisionDetector.BuildPlan(monitors));
    }

    [Fact]
    public void BuildPlan_HandlesAnEmptyInput()
    {
        Assert.Empty(EdidCollisionDetector.BuildPlan(Array.Empty<MonitorEdid>()));
    }

    [Fact]
    public void GeneratedSerials_SitInTheReservedRange()
    {
        var plan = EdidCollisionDetector.BuildPlan(new[]
        {
            Dm7000(EdidFixtures.Dm7000UnitAInstanceId),
            Dm7000(EdidFixtures.Dm7000UnitBInstanceId)
        });

        Assert.All(plan, entry =>
            Assert.True(entry.NewSerialNumber > EdidCollisionDetector.GeneratedSerialBase));
    }

    /// <summary>
    /// The suffix is what distinguishes the panels, so truncation to the 13-byte descriptor
    /// must never eat it.
    /// </summary>
    [Fact]
    public void BuildSerialText_KeepsTheSuffixWhenTruncating()
    {
        var text = EdidCollisionDetector.BuildSerialText(EdidFixtures.Pl2452UnitA(), "A");

        Assert.EndsWith("-A", text);
        Assert.True(text.Length <= EdidBlock.DescriptorTextLength);
    }

    [Fact]
    public void BuildSerialText_FallsBackToTheNumericSerial_WhenNoTextDescriptorExists()
    {
        var text = EdidCollisionDetector.BuildSerialText(EdidFixtures.Dm7000(), "B");

        Assert.Equal("880-B", text);
    }

    /// <summary>
    /// End-to-end over the pure layers: the plan's serials, once applied, must actually make the
    /// identities distinct. Producing a plan whose result still collides would be the one
    /// outcome worse than doing nothing.
    /// </summary>
    [Fact]
    public void AppliedPlan_MakesTheIdentitiesDistinct()
    {
        var monitors = new[]
        {
            Dm7000(EdidFixtures.Dm7000UnitAInstanceId),
            Dm7000(EdidFixtures.Dm7000UnitBInstanceId)
        };

        Assert.Equal(monitors[0].Edid.IdentityKey, monitors[1].Edid.IdentityKey);

        var mutated = EdidCollisionDetector.BuildPlan(monitors)
            .Select(entry => EdidMutator.WithSerialNumber(
                entry.Monitor.Edid, entry.NewSerialNumber, entry.NewSerialText).Edid)
            .ToList();

        Assert.Equal(2, mutated.Select(e => e.IdentityKey).Distinct().Count());
        Assert.All(mutated, e => Assert.True(e.IsChecksumValid));
    }
}
