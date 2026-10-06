using TouchMappingAgent.WPF.Services;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Stands in for <see cref="ITabcalRunner"/> so a test never starts a real process.
///
/// This exists because it once did. SetupWizardViewModel called Process.Start directly, and
/// SetupWizardTests handed it the genuine C:\Windows\System32\tabcal.exe together with the
/// placeholder arguments "LinCal ..." — so every single run of the test suite launched the real
/// calibration tool, which answers a malformed command line with a modal "Ungültige
/// Befehlszeilensyntax" dialog on whatever screen the developer happens to be using. A unit test
/// must not be able to do that, which is why the runner is now injected and this fake is the only
/// implementation the test project ever supplies.
/// </summary>
internal sealed class FakeTabcalRunner : ITabcalRunner
{
    /// <summary>What every call returns. Defaults to a clean run.</summary>
    public TabcalResult Result { get; set; } = new(Success: true, ExitCode: 0, Error: null);

    /// <summary>Every invocation, in order, so a test can assert on the command line built.</summary>
    public List<(string ExecutablePath, string Arguments)> Invocations { get; } = [];

    /// <inheritdoc />
    public Task<TabcalResult> RunAsync(
        string executablePath,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add((executablePath, arguments));
        return Task.FromResult(Result);
    }
}
