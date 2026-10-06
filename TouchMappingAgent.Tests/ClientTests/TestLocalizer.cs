using System.Globalization;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.WPF.Localization;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// A localiser for tests that are about behaviour, not wording.
///
/// It renders a message as its diagnostic form — "Key(arg0, arg1)" — rather than looking up a
/// resource. That keeps assertions pinned to the KEY the code chose, which is the decision under
/// test, instead of to a German or English sentence that a translator is free to reword. A test
/// asserting on the finished sentence would break every time someone improved the phrasing.
///
/// Tests that are genuinely about the resource files use the real <see cref="Localizer"/> — see
/// <see cref="LocalizationTests"/>.
/// </summary>
internal sealed class TestLocalizer : ILocalizer
{
    /// <inheritdoc />
    public CultureInfo CurrentCulture => CultureInfo.InvariantCulture;

    /// <inheritdoc />
    public IReadOnlyList<CultureInfo> AvailableCultures { get; } = [CultureInfo.InvariantCulture];

    /// <inheritdoc />
    public event EventHandler? CultureChanged;

    /// <inheritdoc />
    public string this[string key] => key;

    /// <inheritdoc />
    public string Format(LocalizableText? text) => text?.ToDiagnosticString() ?? string.Empty;

    /// <inheritdoc />
    public void SetCulture(CultureInfo culture) => CultureChanged?.Invoke(this, EventArgs.Empty);
}
