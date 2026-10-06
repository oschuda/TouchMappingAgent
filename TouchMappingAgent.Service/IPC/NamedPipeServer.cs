using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.Service.HealthMonitoring;
using TouchMappingAgent.Service.Integration;
using TouchMappingAgent.Service.Logging;
using TouchMappingAgent.Service.Services;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Service.IPC;

/// <summary>
/// Named Pipe server for handling IPC requests from the WPF client.
/// Runs asynchronously without blocking the service thread.
/// </summary>
public class NamedPipeServer
{
    private const string PipeName = "TouchMappingAgent";
    private const int MaxServerInstances = 10;
    private const int RetryBaseDelayMs = 250;
    private const int MaxRetryDelayMs = 30000;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NamedPipeServer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NamedPipeServer"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider for dependency injection.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public NamedPipeServer(IServiceProvider serviceProvider, ILogger<NamedPipeServer> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts the Named Pipe server and listens for client connections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the server.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipeServer = null;
            try
            {
                // Create secure pipe with restricted ACLs (IEC 62443-4-2 CR-2.1)
                pipeServer = SecureNamedPipeFactory.CreateSecureServerPipe();

                await pipeServer.WaitForConnectionAsync(cancellationToken);

                _logger.LogDebug("NamedPipeServer: client connected");

                // Ownership transfers to HandleClientAsync, which disposes the stream.
                var acceptedPipe = pipeServer;
                pipeServer = null;

                // W-6 fix: log unhandled exceptions instead of silently discarding them.
                // HandleClientAsync now classifies and handles its own disconnect exceptions
                // (see IsExpectedDisconnect), so this continuation is a last-resort safety net
                // for whatever still escapes it — but it classifies too, rather than blanket-
                // logging, so it cannot turn an ordinary disconnect back into a false server
                // fault at the one remaining point where an exception could surface.
                _ = HandleClientAsync(acceptedPipe, cancellationToken)
                    .ContinueWith(
                        t =>
                        {
                            var ex = t.Exception!.GetBaseException();
                            if (IsExpectedDisconnect(ex, cancellationToken, duringTeardown: true))
                                LogClientDisconnect(ex, "Unhandled client disconnect in HandleClientAsync");
                            else
                                LogError(ex, "Unhandled error in HandleClientAsync");
                        },
                        TaskContinuationOptions.OnlyOnFaulted);

                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Internal logging only - never expose to client
                LogError(ex, "Error in Named Pipe server");

                // Back off before retrying. Without this the loop spins as fast as the CPU
                // allows on any persistent creation failure — e.g. the ACL-setting
                // UnauthorizedAccessException raised when the process lacks the privilege to
                // apply the pipe's security descriptor. That burns a core and floods both the
                // log file and the Event Log with thousands of identical entries per second.
                await DelayBeforeRetryAsync(++consecutiveFailures, cancellationToken);
            }
            finally
            {
                // Only set when the connection was never handed off (creation or
                // WaitForConnectionAsync threw); otherwise this is null and disposal is
                // HandleClientAsync's job. Leaving it undisposed leaked a pipe handle per
                // failed iteration.
                pipeServer?.Dispose();
            }
        }
    }

    /// <summary>
    /// Exponential backoff (capped) between failed listen attempts, so a persistent failure
    /// degrades into a slow retry loop rather than a hot spin.
    /// </summary>
    private static async Task DelayBeforeRetryAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        var delayMs = Math.Min(
            RetryBaseDelayMs * (1 << Math.Min(consecutiveFailures - 1, 6)),
            MaxRetryDelayMs);

        await Task.Delay(delayMs, cancellationToken);
    }

    /// <summary>
    /// Handles a single client connection asynchronously.
    /// Includes DoS protection: size-limited reads, exception handling.
    ///
    /// The OUTER try wraps the entire using-block, including disposal — that is deliberate
    /// and is itself the fix for a real defect: StreamReader/StreamWriter/pipeServer dispose
    /// when the using-block unwinds, which happens AFTER any try/catch that lives only inside
    /// the block. A client that vanishes mid-write (an RDP session/desktop switch tearing the
    /// pipe down, a client restart, a crash) makes that disposal-time Flush/Dispose throw
    /// IOException ("Pipe is broken") — and with the try INSIDE the using-block (the previous
    /// shape), that exception escaped HandleClientAsync entirely and only got caught by the
    /// ContinueWith(OnlyOnFaulted) wrapper in StartAsync, which logged it as
    /// "Unhandled error in HandleClientAsync" — an ordinary disconnect, audited as if the
    /// server itself had failed. Wrapping the whole using-block fixes that at its source.
    /// </summary>
    private async Task HandleClientAsync(NamedPipeServerStream pipeServer, CancellationToken cancellationToken)
    {
        try
        {
            // leaveOpen: true on both wrappers — see the matching fix in
            // NamedPipeClientImpl.SendAsync for why: StreamReader/StreamWriter each dispose
            // their underlying stream by default, and since both wrap the same pipeServer
            // here, disposing one first (using-blocks unwind in reverse order) closes
            // pipeServer, then the other's Dispose() throws ObjectDisposedException trying to
            // touch the now-closed pipe.
            using (pipeServer)
            using (var reader = new StreamReader(pipeServer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
            using (var writer = new StreamWriter(pipeServer, new UTF8Encoding(false), 1024, leaveOpen: true))
            {
                string? line;
                const int MaxPayloadSize = 65536; // 64 KB limit per request

                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    try
                    {
                        // DoS protection: Reject oversized payloads
                        if (line.Length > MaxPayloadSize)
                        {
                            LogError(
                                new InvalidOperationException($"Payload exceeds {MaxPayloadSize} bytes"),
                                "DoS protection: Oversized payload rejected");
                            await writer.WriteLineAsync(JsonSerializer.Serialize(
                                new { Success = false, Message = "Request payload too large." }));
                            await writer.FlushAsync(cancellationToken);
                            break; // Terminate this connection
                        }

                        var response = await ProcessRequestAsync(line, cancellationToken);
                        var json = JsonSerializer.Serialize(response);
                        await writer.WriteLineAsync(json);
                        await writer.FlushAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex) when (IsExpectedDisconnect(ex, cancellationToken, duringTeardown: false))
                    {
                        // The client vanished between the write above and this one — not a
                        // server fault. duringTeardown: false here on purpose: unlike the
                        // outer catch below, a bare ObjectDisposedException reaching this
                        // specific catch (mid-request, streams not yet unwinding) is only
                        // classified as expected if cancellation was actually observed.
                        LogClientDisconnect(ex, "Client disconnected while processing a request");
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogError(ex, "Error processing individual request");
                        try
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(
                                new { Success = false, Message = "Request processing failed." }));
                            await writer.FlushAsync(cancellationToken);
                        }
                        catch { /* Ignore write errors */ }
                        break; // Close connection after error
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex, cancellationToken, duringTeardown: true))
        {
            // Covers exactly the disposal-time "Pipe is broken" case described above, plus
            // any equivalent failure from ReadLineAsync itself if the client disconnects
            // between reads rather than mid-write.
            LogClientDisconnect(ex, "Client disconnected (pipe closed during read/write/teardown)");
        }
        catch (Exception ex)
        {
            LogError(ex, "Error handling client connection");
        }
    }

    /// <summary>
    /// Processes an IPC request and routes it to the appropriate handler.
    /// Implements IEC 62443 error isolation: single request failure doesn't crash the server.
    /// Uses "$type" discriminator for safe request routing.
    /// </summary>
    private async Task<object> ProcessRequestAsync(string requestJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            LogError(new ArgumentException("Request JSON is empty"), "Invalid empty request");
            return new { Success = false, Message = "Invalid request format." };
        }

        try
        {
            using (JsonDocument doc = JsonDocument.Parse(requestJson))
            {
                var root = doc.RootElement;

                // Extract the discriminator "$type" field
                if (!root.TryGetProperty("$type", out var typeElement))
                {
                    LogError(
                        new InvalidOperationException("Missing $type discriminator"),
                        "Request missing required $type field");
                    ComplianceAuditLogger.LogCriticalAction(
                        AuditActions.UnauthorizedAccess,
                        "MALFORMED_REQUEST",
                        success: false,
                        errorMessage: "Missing $type discriminator");
                    return new { Success = false, Message = "Invalid request format: missing $type." };
                }

                var requestType = typeElement.GetString() ?? "";
                var payloadElement = root.TryGetProperty("$data", out var data) ? data : root;

                // Route based on discriminator
                return requestType switch
                {
                    "CreateBackup" => await HandleCreateBackupAsync(payloadElement.GetRawText(), cancellationToken),
                    "AdvancedRepair" => await HandleAdvancedRepairAsync(payloadElement.GetRawText(), cancellationToken),
                    "GetTouchDevices" => await HandleGetTouchDevicesAsync(cancellationToken),
                    "GetMonitors" => await HandleGetMonitorsAsync(cancellationToken),
                    "MapTouch" => await HandleMapTouchAsync(payloadElement.GetRawText(), cancellationToken),
                    "ConfirmLocalMapping" => await HandleConfirmLocalMappingAsync(payloadElement.GetRawText(), cancellationToken),
                    "GetMappings" => await WithHandlerAsync(
                        h => h.HandleGetMappingsRequestAsync(cancellationToken),
                        () => new GetMappingsResponse(Array.Empty<TouchMapping>())),
                    "DeleteMapping" => await WithHandlerAsync(
                        h => h.HandleDeleteMappingRequestAsync(payloadElement.GetRawText(), cancellationToken),
                        () => new DeleteMappingResponse(false, "Service unavailable.")),
                    "GetPendingReapply" => await WithHandlerAsync(
                        h => h.HandleGetPendingReapplyRequestAsync(payloadElement.GetRawText(), cancellationToken),
                        () => new GetPendingReapplyResponse(Array.Empty<PendingReapply>())),
                    "ReportReapplyResult" => await WithHandlerAsync(
                        h => h.HandleReportReapplyResultRequestAsync(payloadElement.GetRawText(), cancellationToken),
                        () => new ReportReapplyResultResponse(false, "Service unavailable.")),
                    "GetHardwareStatus" => await WithHandlerAsync(
                        h => h.HandleGetHardwareStatusRequestAsync(payloadElement.GetRawText(), cancellationToken),
                        () => new GetHardwareStatusResponse(0, 0, 0, 0,
                            new[] { LocalizableText.Of(MessageKeys.Service_Unavailable) })),                    "GetEdidTemplates" => await WithHandlerAsync(
                        h => h.HandleGetEdidTemplatesRequestAsync(cancellationToken),
                        () => new GetEdidTemplatesResponse(string.Empty, Array.Empty<EdidTemplateInfo>())),
                    "ApplyEdidTemplate" => await WithHandlerAsync(
                        h => h.HandleApplyEdidTemplateRequestAsync(payloadElement.GetRawText(), cancellationToken),
                        () => new EdidOperationResponse(false, 0, 0, LocalizableText.Of(MessageKeys.Service_Unavailable))),
                    "SynthesizeEdid" => await WithHandlerAsync(
                        h => h.HandleSynthesizeEdidRequestAsync(payloadElement.GetRawText(), cancellationToken),
                        () => new EdidOperationResponse(false, 0, 0, LocalizableText.Of(MessageKeys.Service_Unavailable))),
                    "GetEdidStatus" => await WithHandlerAsync(
                        h => h.HandleGetEdidStatusRequestAsync(cancellationToken),
                        () => new GetEdidStatusResponse(Array.Empty<EdidMonitorStatus>(), 0, 0)),
                    "ResolveEdidCollisions" => await WithHandlerAsync(
                        h => h.HandleResolveEdidCollisionsRequestAsync(payloadElement.GetRawText(), cancellationToken),
                        () => new EdidOperationResponse(false, 0, 0, LocalizableText.Of(MessageKeys.Service_Unavailable))),
                    "RestoreEdidDefaults" => await WithHandlerAsync(
                        h => h.HandleRestoreEdidDefaultsRequestAsync(payloadElement.GetRawText(), cancellationToken),
                        () => new EdidOperationResponse(false, 0, 0, LocalizableText.Of(MessageKeys.Service_Unavailable))),
                    "ConsoleDisplayHeartbeat" => HandleConsoleDisplayHeartbeat(payloadElement.GetRawText()),
                    "ForceConsoleSessionReset" => HandleForceConsoleSessionReset(),
                    _ => new { Success = false, Message = $"Unknown request type: {requestType}" }
                };
            }
        }
        catch (JsonException ex)
        {
            LogError(ex, "JSON deserialization failed");
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.UnauthorizedAccess,
                "MALFORMED_REQUEST",
                success: false,
                errorMessage: "JSON parse error");
            return new { Success = false, Message = "Invalid request format." };
        }
        catch (Exception ex)
        {
            LogError(ex, "Unexpected error processing IPC request");
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.UnauthorizedAccess,
                "REQUEST_ERROR",
                success: false,
                errorMessage: "Internal error");
            return new { Success = false, Message = "An error occurred processing your request." };
        }
    }

    // =========================================================================
    // REQUEST HANDLERS (Route to ComplianceRequestHandler)
    // =========================================================================

    /// <summary>
    /// Resolves <see cref="ComplianceRequestHandler"/> and invokes one of its methods,
    /// falling back to a typed "service unavailable" response.
    ///
    /// NOTE ON THE FALLBACK: it exists for a genuinely unavailable handler, NOT to paper over
    /// a broken composition root. An unresolvable registration now fails at startup
    /// (ValidateOnBuild in Program.cs) rather than reaching this catch — which is what
    /// previously turned a DI defect into "every mapping request quietly fails while the
    /// health check reports the service as healthy".
    /// </summary>
    private async Task<object> WithHandlerAsync<TResponse>(
        Func<ComplianceRequestHandler, Task<TResponse>> invoke,
        Func<TResponse> unavailable)
        where TResponse : class
    {
        try
        {
            if (_serviceProvider.GetService(typeof(ComplianceRequestHandler)) is not ComplianceRequestHandler handler)
            {
                LogError(
                    new InvalidOperationException("ComplianceRequestHandler not registered"),
                    "Handler resolution failed");
                return unavailable();
            }

            return await invoke(handler);
        }
        catch (OperationCanceledException)
        {
            LogError(new OperationCanceledException(), "Request cancelled");
            return unavailable();
        }
        catch (Exception ex)
        {
            LogError(ex, "Error invoking ComplianceRequestHandler");
            return unavailable();
        }
    }

    private async Task<object> HandleCreateBackupAsync(string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            var handler = _serviceProvider.GetService(typeof(ComplianceRequestHandler)) as ComplianceRequestHandler;
            if (handler == null)
            {
                LogError(
                    new InvalidOperationException("ComplianceRequestHandler not registered"),
                    "Handler resolution failed");
                return new CreateBackupResponse(false, null, "Service unavailable.");
            }

            var response = await handler.HandleCreateBackupRequestAsync(requestJson, cancellationToken);
            return response;
        }
        catch (OperationCanceledException)
        {
            LogError(new OperationCanceledException(), "CreateBackup request cancelled");
            return new CreateBackupResponse(false, null, "Request cancelled.");
        }
        catch (Exception ex)
        {
            LogError(ex, "Error in HandleCreateBackupAsync");
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.SaveMapping,
                "BACKUP_HANDLER_ERROR",
                success: false,
                errorMessage: "Handler execution failed");

            return new CreateBackupResponse(false, null, "Backup operation failed. Please try again.");
        }
    }

    private async Task<object> HandleAdvancedRepairAsync(string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            var handler = _serviceProvider.GetService(typeof(ComplianceRequestHandler)) as ComplianceRequestHandler;
            if (handler == null)
            {
                LogError(
                    new InvalidOperationException("ComplianceRequestHandler not registered"),
                    "Handler resolution failed");
                return new AdvancedRepairResponse(false, "Service unavailable.", null);
            }

            var response = await handler.HandleAdvancedRepairRequestAsync(requestJson, cancellationToken);
            return response;
        }
        catch (OperationCanceledException)
        {
            LogError(new OperationCanceledException(), "AdvancedRepair request cancelled");
            return new AdvancedRepairResponse(false, "Request cancelled.", null);
        }
        catch (Exception ex)
        {
            LogError(ex, "Error in HandleAdvancedRepairAsync");
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.StartAdvancedRepair,
                "REPAIR_HANDLER_ERROR",
                success: false,
                errorMessage: "Handler execution failed");

            return new AdvancedRepairResponse(
                false,
                "Repair sequence encountered an error.",
                "Check Windows Event Log for details.");
        }
    }

    private Task<object> HandleGetTouchDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = TouchDigitizerEnumerator.EnumerateDigitizers();
            return Task.FromResult<object>(new GetTouchDevicesResponse(devices));
        }
        catch (Exception ex)
        {
            LogError(ex, "Error in HandleGetTouchDevicesAsync");
            return Task.FromResult<object>(new GetTouchDevicesResponse(new List<HidDeviceInfo>()));
        }
    }

    /// <summary>
    /// Enumerates physical monitors via Win32 (EnumDisplayDevices/EnumDisplaySettings).
    ///
    /// NOTE: confirmed empirically that this returns an EMPTY list when run from this SYSTEM/
    /// Session-0 service process, even on a machine with real monitors attached — the same
    /// Session-0 visibility gap already known from tabcal.exe. The WPF client therefore calls
    /// TouchMappingAgent.Shared.Hardware.DisplayEnumerator directly instead of going through
    /// this IPC command (see MonitorMappingViewModel.LoadMonitors). Kept here for any other/
    /// future consumer of the IPC API, but it should not be relied on for real monitor data.
    /// </summary>
    private Task<object> HandleGetMonitorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var monitors = DisplayEnumerator.EnumerateMonitors();
            return Task.FromResult<object>(new GetMonitorsResponse(monitors));
        }
        catch (Exception ex)
        {
            LogError(ex, "Error in HandleGetMonitorsAsync");
            return Task.FromResult<object>(new GetMonitorsResponse(new List<MonitorInfo>()));
        }
    }

    /// <summary>
    /// Records the outcome of a tabcal.exe mapping/calibration that the WPF client executed
    /// locally in the interactive user session (the service itself cannot run it — Session 0
    /// isolation prevents a service process from showing UI or receiving a physical touch
    /// confirmation). This closes the audit-trail loop for the delegated execution.
    /// </summary>
    private async Task<object> HandleConfirmLocalMappingAsync(string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            var handler = _serviceProvider.GetService(typeof(ComplianceRequestHandler)) as ComplianceRequestHandler;
            if (handler == null)
            {
                LogError(
                    new InvalidOperationException("ComplianceRequestHandler not registered"),
                    "Handler resolution failed");
                return new ConfirmLocalMappingResponse(false, "Service unavailable.");
            }

            return await handler.HandleConfirmLocalMappingRequestAsync(requestJson, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogError(new OperationCanceledException(), "ConfirmLocalMapping request cancelled");
            return new ConfirmLocalMappingResponse(false, "Request cancelled.");
        }
        catch (Exception ex)
        {
            LogError(ex, "Error in HandleConfirmLocalMappingAsync");
            return new ConfirmLocalMappingResponse(false, "Failed to record mapping confirmation.");
        }
    }

    private async Task<object> HandleMapTouchAsync(string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            var handler = _serviceProvider.GetService(typeof(ComplianceRequestHandler)) as ComplianceRequestHandler;
            if (handler == null)
            {
                LogError(
                    new InvalidOperationException("ComplianceRequestHandler not registered"),
                    "Handler resolution failed");
                return new MapTouchResponse(false, "Service unavailable.");
            }

            var response = await handler.HandleMapTouchRequestAsync(requestJson, cancellationToken);
            return response;
        }
        catch (OperationCanceledException)
        {
            LogError(new OperationCanceledException(), "MapTouch request cancelled");
            return new MapTouchResponse(false, "Request cancelled.");
        }
        catch (Exception ex)
        {
            LogError(ex, "Error in HandleMapTouchAsync");
            return new MapTouchResponse(false, "Mapping operation failed. Please try again.");
        }
    }

    /// <summary>
    /// Records the interactive client's self-reported display count for the console session
    /// watchdog (<see cref="ConsoleSessionWatchdog"/>). See
    /// <see cref="ConsoleDisplayHeartbeatState"/>'s remarks for why the service cannot measure
    /// this itself. Deliberately bypassed for ComplianceRequestHandler/audit logging — this
    /// fires every ~15s from a healthy client and is operational telemetry, not a user action.
    /// </summary>
    private object HandleConsoleDisplayHeartbeat(string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ConsoleDisplayHeartbeatRequest>(requestJson);
            if (request == null)
                return new ConsoleDisplayHeartbeatResponse(false);

            if (_serviceProvider.GetService(typeof(ConsoleDisplayHeartbeatState)) is ConsoleDisplayHeartbeatState state)
            {
                state.Report(request.SessionId, request.MonitorCount, DateTimeOffset.UtcNow);
                return new ConsoleDisplayHeartbeatResponse(true);
            }

            LogError(
                new InvalidOperationException("ConsoleDisplayHeartbeatState not registered"),
                "Handler resolution failed");
            return new ConsoleDisplayHeartbeatResponse(false);
        }
        catch (Exception ex)
        {
            LogError(ex, "Error in HandleConsoleDisplayHeartbeat");
            return new ConsoleDisplayHeartbeatResponse(false);
        }
    }

    /// <summary>
    /// Manual operator override ("Diagnose &gt; Konsolen-Sitzung zurücksetzen"): runs the exact
    /// same guarded reset the watchdog runs automatically, immediately, bypassing the boot
    /// hysteresis and debounce timers but not the underlying WinStationName=="Console" safety
    /// check in <see cref="ConsoleSessionResetService"/>.
    /// </summary>
    private object HandleForceConsoleSessionReset()
    {
        try
        {
            if (_serviceProvider.GetService(typeof(ConsoleSessionResetService)) is not ConsoleSessionResetService resetService)
            {
                LogError(
                    new InvalidOperationException("ConsoleSessionResetService not registered"),
                    "Handler resolution failed");
                return new ForceConsoleSessionResetResponse(false, "Service unavailable.");
            }

            var outcome = resetService.ResetConsoleSession("manual operator override");
            return new ForceConsoleSessionResetResponse(
                outcome == ConsoleResetOutcome.Success,
                outcome.ToString());
        }
        catch (Exception ex)
        {
            LogError(ex, "Error in HandleForceConsoleSessionReset");
            return new ForceConsoleSessionResetResponse(false, "Reset operation failed.");
        }
    }

    // Win32 error codes that mean "the other end of the pipe is gone", surfaced through
    // IOException.HResult. These are the ONLY codes IsExpectedPipeDisconnect treats as
    // harmless — any other IOException is still a genuine server-side fault.
    internal const int ErrorBrokenPipe = 109;       // ERROR_BROKEN_PIPE
    internal const int ErrorNoData = 232;           // ERROR_NO_DATA (write to a pipe whose reader closed)
    internal const int ErrorPipeNotConnected = 233; // ERROR_PIPE_NOT_CONNECTED

    /// <summary>
    /// True only for the specific Win32 error codes that mean the client end of the pipe is
    /// gone. Deliberately narrow: an earlier version of this classification treated EVERY
    /// IOException as an ordinary disconnect, which would also have swallowed a genuine disk-
    /// full or handle-exhaustion failure surfacing through the same exception type. Checking
    /// the HRESULT's facility bits (FACILITY_WIN32 = 0x8007xxxx) first guards against an
    /// IOException whose HResult was never actually set from a Win32 error at all.
    ///
    /// Internal (not private) so this classification — the actual fix for the RDP-session
    /// Event Log spam — is unit-tested directly, including the regression case: an IOException
    /// with an unrelated HResult must still be treated as a genuine fault.
    /// </summary>
    internal static bool IsExpectedPipeDisconnect(IOException ex)
    {
        if ((ex.HResult & unchecked((int)0xFFFF0000)) != unchecked((int)0x80070000))
            return false;

        return (ex.HResult & 0xFFFF) is ErrorBrokenPipe or ErrorNoData or ErrorPipeNotConnected;
    }

    /// <summary>
    /// Classifies a failure as an ordinary client disconnect (log at Debug, no Event Log
    /// entry) versus a genuine server fault (log at Error, audit trail entry). This is the
    /// fix for RDP-session churn — and, just as much, plain WPF-client restarts, a user
    /// switch, or the client crashing — spamming the Windows Event Log as UNAUTHORIZED_ACCESS/
    /// PipeServerError entries for what is, every time, the other side of the pipe going away.
    ///
    /// <paramref name="duringTeardown"/> widens what counts as expected for
    /// ObjectDisposedException: disposing pipe/reader/writer after the client vanished can
    /// legitimately throw ObjectDisposedException even without cancellation being requested
    /// (the pipe object itself is already torn down from the OS side), whereas the same
    /// exception appearing WHILE actively processing a request (duringTeardown: false) is
    /// only expected if it lines up with an actual, observed cancellation.
    /// </summary>
    internal static bool IsExpectedDisconnect(Exception ex, CancellationToken ct, bool duringTeardown) => ex switch
    {
        IOException io => IsExpectedPipeDisconnect(io),
        OperationCanceledException => ct.IsCancellationRequested,
        ObjectDisposedException => duringTeardown || ct.IsCancellationRequested,
        _ => false
    };

    /// <summary>
    /// Logs an ordinary client disconnect at Debug level only — deliberately NOT routed
    /// through ComplianceAuditLogger, so it never reaches the Windows Event Log. This is the
    /// counterpart to <see cref="LogError"/> for exactly the failures
    /// <see cref="IsExpectedDisconnect"/> classifies as harmless.
    /// </summary>
    private void LogClientDisconnect(Exception ex, string context) =>
        _logger.LogDebug(ex, "NamedPipeServer: {Context} (ordinary client disconnect)", context);

    /// <summary>
    /// Internal error logging with audit trail integration.
    /// Logs to ILogger and ComplianceAuditLogger (ISO 27001 A.8.15).
    ///
    /// Takes a plain message, NOT a format string: the previous version ran every message
    /// through string.Format, which throws FormatException on the structured-logging
    /// placeholders ("{DeviceId}") used everywhere else in this codebase — inside the very
    /// catch blocks that exist to keep the pipe server alive.
    ///
    /// AUDITS AS PipeServerError, NOT UnauthorizedAccess: this is the generic catch-all used
    /// for every kind of failure in this class — pipe listen errors, DI handler-resolution
    /// failures, and (this is the one that matters) an abrupt client disconnect surfacing as
    /// an unhandled IOException while disposing the pipe/reader/writer, logged from the outer
    /// "Unhandled error in HandleClientAsync" wrapper. None of those are an access-control
    /// violation, and auditing them as UNAUTHORIZED_ACCESS made ordinary RDP-session
    /// disconnect churn look like a security incident in the audit trail. The call sites that
    /// DO detect something genuinely security-relevant (a missing "$type" discriminator, a
    /// JSON parse failure on the request envelope) log their own explicit UnauthorizedAccess
    /// entry immediately next to their LogError call — unaffected by this.
    /// </summary>
    private void LogError(Exception ex, string message)
    {
        try
        {
            _logger.LogError(ex, "NamedPipeServer: {FailureContext}", message);

            // Audit trail (ISO 27001) — infrastructure/operational error, not an access
            // violation. See the method doc comment above.
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.PipeServerError,
                "PIPE_SERVER",
                success: false,
                errorMessage: message);
        }
        catch
        {
            // Prevent logging errors from cascading
            System.Diagnostics.Debug.WriteLine($"[NamedPipeServer] Logging itself failed: {ex}");
        }
    }
}

