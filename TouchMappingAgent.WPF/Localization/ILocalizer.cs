using System.Globalization;
using TouchMappingAgent.Shared.Localization;

namespace TouchMappingAgent.WPF.Localization;

/// <summary>
/// Resolves message keys to text in the operator's language.
///
/// This lives in the client and nowhere else. The service runs as LocalSystem in Session 0 and
/// has no way to know which language the logged-in operator reads, so every piece of
/// operator-facing text it produces arrives here as a <see cref="LocalizableText"/> and is
/// formatted at this point.
/// </summary>
public interface ILocalizer
{
    /// <summary>The culture currently used for lookups.</summary>
    CultureInfo CurrentCulture { get; }

    /// <summary>
    /// The languages this build actually ships, in display order. Derived from the satellite
    /// assemblies present on disk rather than a hard-coded list, so adding a translation does not
    /// require touching the language picker.
    /// </summary>
    IReadOnlyList<CultureInfo> AvailableCultures { get; }

    /// <summary>
    /// Looks up a key. A key with no translation returns a visible placeholder rather than an
    /// empty string — a blank label on a commissioning screen is indistinguishable from a broken
    /// one, whereas "[Some_Key]" can be read out over the phone and traced.
    /// </summary>
    string this[string key] { get; }

    /// <summary>
    /// Resolves a message that crossed the pipe, substituting its arguments into the localised
    /// format string.
    /// </summary>
    string Format(LocalizableText? text);

    /// <summary>
    /// Switches language at runtime and persists the choice for this user.
    /// </summary>
    void SetCulture(CultureInfo culture);

    /// <summary>Raised after <see cref="SetCulture"/> changes the language.</summary>
    event EventHandler? CultureChanged;
}
