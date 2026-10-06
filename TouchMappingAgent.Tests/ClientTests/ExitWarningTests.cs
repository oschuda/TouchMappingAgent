using System.Globalization;
using TouchMappingAgent.WPF.Localization;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Pins the exact German wording of the shutdown warning.
///
/// This text is not decorative. It is the only place the operator is told that stopping the
/// agent removes the mechanism that keeps touch input on the right screen — Windows itself
/// cannot maintain the assignment for structurally identical monitors. Rewording it silently
/// would remove the one warning standing between a well-meant "Beenden" click and touches
/// landing on the wrong panel after the next restart.
///
/// Reads the German entry straight out of the shipped Strings.resx, by culture, rather than
/// through a Localizer instance — a Localizer picks its culture from the machine it runs on, and
/// this test must check the agreed German wording regardless of that.
/// </summary>
public class ExitWarningTests
{
    private static string GermanExitWarning() =>
        Strings.ResourceManager.GetString(
            LocalizationKeys.Tray_ExitWarning, CultureInfo.GetCultureInfo("de"))!;

    [Fact]
    public void ExitWarning_MatchesTheAgreedWordingExactly()
    {
        const string expected =
            "WICHTIGER HINWEIS:\n" +
            "Windows unterstützt systembedingt keine dauerhafte Zuordnung von Touch-Digitizern zu baugleichen Monitoren.\n" +
            "\n" +
            "Wenn Sie diesen Agenten beenden, kann das System die Zuordnung bei Neustarts oder DisplayPort-Events nicht mehr aufrechterhalten (Touch-Eingaben landen auf dem falschen Bildschirm).\n" +
            "\n" +
            "Möchten Sie den Agenten wirklich beenden?";

        Assert.Equal(expected, GermanExitWarning());
    }

    [Fact]
    public void ExitWarning_KeepsTheParagraphBreaksThatMakeItReadable()
    {
        var paragraphs = GermanExitWarning().Split("\n\n");

        Assert.Equal(3, paragraphs.Length);
        Assert.StartsWith("WICHTIGER HINWEIS:", paragraphs[0]);
        Assert.EndsWith("wirklich beenden?", paragraphs[2]);
    }

    [Fact]
    public void ExitWarning_NamesTheConcreteConsequence()
    {
        var message = GermanExitWarning();

        Assert.Contains("Neustarts", message);
        Assert.Contains("DisplayPort-Events", message);
        Assert.Contains("falschen Bildschirm", message);
    }
}
