namespace TouchMappingAgent.Shared.Contracts;

/// <summary>
/// Interface for communication via Named Pipes to the background service.
/// </summary>
public interface INamedPipeClient
{
    /// <summary>
    /// Sends a request to the service and waits for a response, using the default read timeout.
    /// </summary>
    /// <typeparam name="TResponse">The expected response type.</typeparam>
    /// <param name="request">The request object.</param>
    /// <returns>The response from the service.</returns>
    Task<TResponse> SendAsync<TResponse>(object request) where TResponse : class;

    /// <summary>
    /// Sends a request to the service and waits for a response, overriding the default read
    /// timeout. Use for requests whose server-side handling can legitimately run longer than the
    /// default (e.g. AdvancedRepair, which may invoke tabcal.exe with its own 30-second wait).
    /// </summary>
    /// <typeparam name="TResponse">The expected response type.</typeparam>
    /// <param name="request">The request object.</param>
    /// <param name="responseTimeout">The read timeout to use for this call.</param>
    /// <returns>The response from the service.</returns>
    Task<TResponse> SendAsync<TResponse>(object request, TimeSpan responseTimeout) where TResponse : class;

    /// <summary>
    /// Checks if the connection to the service is available.
    /// </summary>
    /// <returns>True if connected; otherwise false.</returns>
    Task<bool> IsConnectedAsync();
}
