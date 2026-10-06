namespace Evolved.EdidManager.Edid;

/// <summary>
/// Finds monitors that are indistinguishable by EDID and assigns each a unique replacement
/// serial number.
///
/// Pure logic over an input list — no registry, no device tree — so the allocation rules can
/// be tested directly.
/// </summary>
public static class EdidCollisionDetector
{
    /// <summary>
    /// Base value for generated serials. Deliberately far away from typical factory serials so
    /// a mutated panel is recognisable as such in a survey, and so the generated range cannot
    /// collide with a real serial that happens to be small.
    /// </summary>
    public const uint GeneratedSerialBase = 900_000_000;

    /// <summary>
    /// Groups the input by EDID identity and produces a plan for every monitor that shares its
    /// identity with at least one other.
    ///
    /// The FIRST monitor of a colliding group is included in the plan as well, rather than being
    /// left at its factory serial. Leaving one member untouched would work, but it makes the
    /// result depend on enumeration order: on the next run a different panel could be "the
    /// first" and the assignment would shuffle. Giving every member of a group a deterministic,
    /// derived serial keeps the outcome stable across reboots.
    /// </summary>
    /// <param name="monitors">Monitors with a readable EDID, in any order.</param>
    public static IReadOnlyList<CollisionPlanEntry> BuildPlan(IReadOnlyList<MonitorEdid> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        var plan = new List<CollisionPlanEntry>();

        var groups = monitors
            .GroupBy(m => m.Edid.IdentityKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var group in groups)
        {
            // Ordering by PnP instance id, not by enumeration order: the instance id is derived
            // from the physical connector and is stable, so the same panel gets the same
            // generated serial on every run.
            var ordered = group.OrderBy(m => m.PnpInstanceId, StringComparer.OrdinalIgnoreCase).ToList();

            for (int index = 0; index < ordered.Count; index++)
            {
                var monitor = ordered[index];
                uint serial = GeneratedSerialBase + (uint)index + 1;
                string suffix = ((char)('A' + index)).ToString();

                plan.Add(new CollisionPlanEntry(
                    Monitor: monitor,
                    NewSerialNumber: serial,
                    NewSerialText: BuildSerialText(monitor.Edid, suffix),
                    GroupSize: ordered.Count,
                    IndexInGroup: index));
            }
        }

        return plan;
    }

    /// <summary>
    /// Reports monitors whose NUMERIC serial (bytes 12-15) collides but whose 0xFF text
    /// descriptor still tells them apart. These are deliberately NOT rewritten.
    ///
    /// Why this distinction is not academic: 0x01010101 is a widespread "serial not programmed"
    /// placeholder. Two iiyama PL2452 panels on the development machine both report it, yet
    /// carry distinct serial texts ("1128533500538" and "1128533500607") and are perfectly
    /// distinguishable. Treating a shared numeric placeholder as a collision would rewrite the
    /// EDID of a great many panels that have no problem at all.
    ///
    /// The DM7000 case is the one that genuinely needs fixing: same numeric serial AND no
    /// distinguishing text, so nothing separates them.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<MonitorEdid>> FindNumericOnlyCollisions(
        IReadOnlyList<MonitorEdid> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        return monitors
            .GroupBy(m => $"{m.Edid.ManufacturerCode}:{m.Edid.ProductCode:X4}:{m.Edid.SerialNumber:X8}",
                StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            // Only groups the full-identity check did NOT already claim: if the texts match too,
            // BuildPlan handles it.
            .Where(group => group.Select(m => m.Edid.IdentityKey).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => (IReadOnlyList<MonitorEdid>)group.ToList())
            .ToList();
    }

    /// <summary>
    /// Derives the replacement serial text: the original text (or the numeric serial when the
    /// panel has no text descriptor) plus a group suffix — "880" becomes "880-A" and "880-B".
    /// Truncated to fit the 13-byte descriptor field with its suffix intact, because the suffix
    /// is the part that carries the distinction.
    /// </summary>
    internal static string BuildSerialText(EdidBlock edid, string suffix)
    {
        var basis = string.IsNullOrWhiteSpace(edid.SerialText)
            ? edid.SerialNumber.ToString()
            : edid.SerialText!;

        int room = EdidBlock.DescriptorTextLength - suffix.Length - 1;
        if (basis.Length > room)
            basis = basis[..Math.Max(0, room)];

        return $"{basis}-{suffix}";
    }
}

/// <summary>A monitor with its PnP identity and the EDID read from it.</summary>
/// <param name="PnpInstanceId">e.g. "DISPLAY\CHR8910\5&amp;2c72b841&amp;0&amp;UID250116".</param>
/// <param name="Edid">The EDID currently reported by that monitor.</param>
/// <param name="IsOverridden">True when the EDID came from an existing override rather than hardware.</param>
public sealed record MonitorEdid(string PnpInstanceId, EdidBlock Edid, bool IsOverridden = false);

/// <summary>One monitor's entry in a de-collision plan.</summary>
/// <param name="Monitor">The monitor to modify.</param>
/// <param name="NewSerialNumber">Replacement numeric serial for bytes 12-15.</param>
/// <param name="NewSerialText">Replacement text for the 0xFF descriptor, when present.</param>
/// <param name="GroupSize">How many monitors shared this identity.</param>
/// <param name="IndexInGroup">Zero-based position within the group.</param>
public sealed record CollisionPlanEntry(
    MonitorEdid Monitor,
    uint NewSerialNumber,
    string NewSerialText,
    int GroupSize,
    int IndexInGroup);
