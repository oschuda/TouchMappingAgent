using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TouchMappingAgent.Shared.Contracts;

/// <summary>
/// Concrete implementation of INamedPipeClient for WPF client communication with service.
/// Uses secure Named Pipes with proper error handling and timeout management.
/// </summary>
public class NamedPipeClientImpl : INamedPipeClient
{
    private const string PipeName = "TouchMappingAgent"; // N-1 fix: must match SecureNamedPipeFactory.PipeName
    private const string ServerName = ".";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadWriteTimeout = TimeSpan.FromSeconds(10);
    private const int MaxResponseSize = 65536; // 64 KB max response

    /// <inheritdoc/>
    public async Task<bool> IsConnectedAsync()
    {
        try
        {
            using (var client = new NamedPipeClientStream(ServerName, PipeName, PipeDirection.InOut))
            {
                // Try to connect with short timeout
                await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds);
                return true;
            }
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("Service connection timeout");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            Debug.WriteLine("Service access denied - insufficient permissions");
            return false;
        }
        catch (IOException)
        {
            Debug.WriteLine("Service pipe not available");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unexpected error checking service connection: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public Task<TResponse> SendAsync<TResponse>(object request) where TResponse : class =>
        SendAsync<TResponse>(request, ReadWriteTimeout);

    /// <inheritdoc/>
    public async Task<TResponse> SendAsync<TResponse>(object request, TimeSpan responseTimeout) where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // Build request envelope with $type discriminator
            var requestEnvelope = BuildRequestEnvelope(request);

            using (var client = new NamedPipeClientStream(ServerName, PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try
                {
                    // Connect with timeout
                    await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds);
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException("Could not connect to service. Is the service running?", null);
                }
                catch (UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("Insufficient permissions to access the service.", null);
                }

                // leaveOpen: true on BOTH wrappers is required. StreamWriter/StreamReader each
                // dispose their underlying stream by default; since both wrap the same `client`
                // pipe here, whichever is disposed first (using-blocks unwind in reverse
                // declaration order, so `reader` first) closes `client` — then the second one's
                // Dispose() (StreamWriter always flushes on Dispose, even with AutoFlush) tries
                // to touch the now-closed pipe and throws ObjectDisposedException. This happened
                // on EVERY call, discarding an already-successfully-read response: confirmed
                // empirically — Write and ReadLineAsync both completed and returned real data,
                // then disposal threw and the caller never saw it. This is what surfaced as
                // "Service is not responding" / monitors and touch devices never loading, even
                // once the server itself was reachable and answering correctly.
                using (var writer = new StreamWriter(client, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true })
                using (var reader = new StreamReader(client, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
                {
                    // Send request
                    var requestJson = JsonSerializer.Serialize(requestEnvelope, DefaultJsonOptions);
                    await writer.WriteLineAsync(requestJson);

                    // N-2 fix: async read with CancellationToken-based timeout (no .Wait() = no deadlock)
                    string? responseLine;
                    using (var readCts = new CancellationTokenSource(responseTimeout))
                    {
                        try
                        {
                            responseLine = await reader.ReadLineAsync(readCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            throw new InvalidOperationException("Service response timeout", null);
                        }
                    }
                    if (string.IsNullOrEmpty(responseLine))
                    {
                        throw new InvalidOperationException("Empty response from service", null);
                    }

                    // DoS protection: check response size
                    if (responseLine.Length > MaxResponseSize)
                    {
                        throw new InvalidOperationException("Response too large", null);
                    }

                    // Deserialize response
                    var response = JsonSerializer.Deserialize<TResponse>(responseLine, DefaultJsonOptions);
                    if (response == null)
                    {
                        throw new InvalidOperationException("Could not deserialize service response", null);
                    }

                    return response;
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error communicating with service: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds the request envelope with $type discriminator for proper routing on server.
    /// </summary>
    private static object BuildRequestEnvelope(object request)
    {
        var typeName = request.GetType().Name;

        // Map request type to discriminator value
        var discriminator = typeName switch
        {
            nameof(GetTouchDevicesRequest) => "GetTouchDevices",
            nameof(GetMonitorsRequest) => "GetMonitors",
            nameof(MapTouchRequest) => "MapTouch",
            nameof(ConfirmLocalMappingRequest) => "ConfirmLocalMapping",
            nameof(GetMappingsRequest) => "GetMappings",
            nameof(DeleteMappingRequest) => "DeleteMapping",
            nameof(GetPendingReapplyRequest) => "GetPendingReapply",
            nameof(ApplyMappingsNowRequest) => "ApplyMappingsNow",
            nameof(ReportReapplyResultRequest) => "ReportReapplyResult",
            nameof(GetHardwareStatusRequest) => "GetHardwareStatus",
            nameof(GetEdidStatusRequest) => "GetEdidStatus",
            nameof(GetEdidTemplatesRequest) => "GetEdidTemplates",
            nameof(ApplyEdidTemplateRequest) => "ApplyEdidTemplate",
            nameof(SynthesizeEdidRequest) => "SynthesizeEdid",
            nameof(ResolveEdidCollisionsRequest) => "ResolveEdidCollisions",
            nameof(RestoreEdidDefaultsRequest) => "RestoreEdidDefaults",
            nameof(ConsoleDisplayHeartbeatRequest) => "ConsoleDisplayHeartbeat",
            nameof(ForceConsoleSessionResetRequest) => "ForceConsoleSessionReset",
            _ => typeName
        };

        // Build envelope as Dictionary to support $ prefix in JSON
        var envelope = new Dictionary<string, object>
        {
            { "$type", discriminator },
            { "$data", request }
        };

        return envelope;
    }

    // O-2 fix: cache JsonSerializerOptions instance (creation is expensive)
    private static readonly JsonSerializerOptions DefaultJsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        MaxDepth = 32,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

