namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Outcome of one tabcal.exe invocation.
/// </summary>
/// <param name="Success">
/// True only when the process ran to completion with exit code 0. Note that this is NOT the same
/// as "the screen was calibrated": tabcal.exe returns 0 even when it matched no device and did
/// nothing. Callers must check the digitizer is present before treating this as meaningful — see
/// <see cref="ReapplyAgent.IsTouchDevicePresent"/>.
/// </param>
/// <param name="ExitCode">The process exit code, or null when it never started or was killed.</param>
/// <param name="Error">Diagnostic detail when <paramref name="Success"/> is false; never localised.</param>
public sealed record TabcalResult(bool Success, int? ExitCode, string? Error);

/// <summary>
/// Runs tabcal.exe.
///
/// WHY THIS IS AN INTERFACE: launching tabcal.exe is a side effect on the machine's desktop, and
/// it must never happen from a unit test. Before this seam existed both SetupWizardViewModel and
/// ReapplyAgent called Process.Start directly, and a test that supplied the real
/// C:\Windows\System32\tabcal.exe with placeholder arguments started the actual tool — which
/// answers an invalid command line with a modal "Ungültige Befehlszeilensyntax" dialog on whatever
/// screen the developer is working on. Tests now inject a fake and no process is ever created.
///
/// It also removes a duplicate: the two callers each carried their own copy of the same
/// launch-and-wait logic, and only one of them had the timeout right.
/// </summary>
public interface ITabcalRunner
{
    /// <summary>
    /// Starts tabcal.exe and waits for it to exit.
    /// </summary>
    /// <param name="executablePath">Full path to tabcal.exe.</param>
    /// <param name="arguments">The command line, as built by MappingResolver.</param>
    /// <param name="cancellationToken">Cancels the wait; the process is killed if still running.</param>
    Task<TabcalResult> RunAsync(
        string executablePath,
        string arguments,
        CancellationToken cancellationToken = default);
}
