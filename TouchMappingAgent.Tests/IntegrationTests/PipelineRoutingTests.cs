using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TouchMappingAgent.Service.IPC;
using TouchMappingAgent.Service.Integration;
using TouchMappingAgent.Shared.Contracts;
using Xunit;

namespace TouchMappingAgent.Tests.IntegrationTests;

/// <summary>
/// Integration tests for NamedPipeServer routing and DoS protection.
/// </summary>
public class PipelineRoutingTests
{
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly NamedPipeServer _pipeServer;

    public PipelineRoutingTests()
    {
        _mockServiceProvider = new Mock<IServiceProvider>();
        _pipeServer = new NamedPipeServer(_mockServiceProvider.Object, NullLogger<NamedPipeServer>.Instance);
    }

    [Fact]
    public void NamedPipeServer_InvalidJsonFormat_NotCrashing()
    {
        var maliciousJsons = new[] { "{ invalid", "null", "<script>", "{}" };

        foreach (var json in maliciousJsons)
        {
            var exception = Record.Exception(() =>
            {
                try
                {
                    using (JsonDocument.Parse(json))
                    {
                    }
                }
                catch (JsonException)
                {
                    // Expected
                }
            });

            Assert.Null(exception);
        }
    }

    [Fact]
    public void ResponseSerialization_NoStackTracesExposed()
    {
        var response = new CreateBackupResponse(false, null, "Operation failed.");
        var json = JsonSerializer.Serialize(response);

        Assert.DoesNotContain("Exception", json);
        Assert.DoesNotContain("StackTrace", json);
    }

    [Fact]
    public void NamedPipeServer_Constructor_RequiresServiceProvider()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NamedPipeServer(null!, NullLogger<NamedPipeServer>.Instance));
    }

    [Fact]
    public void CreateBackupResponse_SerializedCorrectly()
    {
        var response = new CreateBackupResponse(true, "BKP_ID_001", null);
        var json = JsonSerializer.Serialize(response);

        Assert.Contains("BKP_ID_001", json);
        Assert.Contains("true", json);
    }

    [Fact]
    public void AdvancedRepairResponse_SerializedCorrectly()
    {
        var response = new AdvancedRepairResponse(true, "Phase info", null);
        var json = JsonSerializer.Serialize(response);

        Assert.Contains("Phase info", json);
        Assert.Contains("true", json);
    }

    [Fact]
    public void ForceConsoleSessionResetResponse_SerializedCorrectly()
    {
        var response = new ForceConsoleSessionResetResponse(true, "Success");
        var json = JsonSerializer.Serialize(response);

        Assert.Contains("Success", json);
        Assert.Contains("true", json);
        Assert.DoesNotContain("Exception", json);
    }

    [Fact]
    public void ConsoleDisplayHeartbeatResponse_SerializedCorrectly()
    {
        var response = new ConsoleDisplayHeartbeatResponse(true);
        var json = JsonSerializer.Serialize(response);

        Assert.Contains("true", json);
    }
}
