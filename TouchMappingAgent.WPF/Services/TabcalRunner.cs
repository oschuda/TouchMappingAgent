using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// The real <see cref="ITabcalRunner"/>: starts C:\Windows\System32\tabcal.exe and waits.
///
/// This is the only place in the client that creates a tabcal.exe process. It must stay that way
/// — see the interface for what happened when it was not.
/// </summary>
public sealed class TabcalRunner : ITabcalRunner
{
    /// <summary>
    /// How long tabcal may take. Generous, because the interactive calibration waits for the
    /// operator to touch crosshairs; a hung process must still not block the agent forever.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<TabcalRunner> _logger;

    /// <summary>Initializes a new instance of <see cref="TabcalRunner"/>.</summary>
    public TabcalRunner(ILogger<TabcalRunner> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<TabcalResult> RunAsync(
        string executablePath,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
            return new TabcalResult(false, null, $"tabcal.exe not found at {executablePath}");

        // Logged in full because the command line is the single most useful thing to have when a
        // calibration misbehaves on a machine we cannot reach — and because tabcal answers a
        // malformed one with a modal dialog rather than a message we could capture.
        _logger.LogInformation("Running: {Executable} {Arguments}", executablePath, arguments);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process == null)
                return new TabcalResult(false, null, "Process.Start returned null");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                return new TabcalResult(
                    false, null, $"tabcal.exe did not finish within {Timeout.TotalSeconds}s");
            }

            return new TabcalResult(
                process.ExitCode == 0,
                process.ExitCode,
                process.ExitCode == 0 ? null : $"tabcal.exe exited with code {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TabcalResult(false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
