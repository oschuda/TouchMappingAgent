using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TouchMappingAgent.Service.Integration;
using TouchMappingAgent.Service.Services;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Unit tests for AdvancedRepairService focusing on partial success and error isolation.
/// </summary>
public class AdvancedRepairServiceTests
{
    [Fact]
    public async Task ExecuteAdvancedRepair_DoesNotThrowException()
    {
        var displayRefresh = new DisplayRefreshService(NullLogger<DisplayRefreshService>.Instance);

        var service = new AdvancedRepairService(NullLogger<AdvancedRepairService>.Instance, displayRefresh);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await service.ExecuteAdvancedRepairAsync();
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task ExecuteAdvancedRepair_ReturnsValidTuple()
    {
        var displayRefresh = new DisplayRefreshService(NullLogger<DisplayRefreshService>.Instance);

        var service = new AdvancedRepairService(NullLogger<AdvancedRepairService>.Instance, displayRefresh);
        var (success, details) = await service.ExecuteAdvancedRepairAsync();

        Assert.IsType<bool>(success);
        Assert.IsType<string>(details);
        Assert.NotEmpty(details);
    }

    [Fact]
    public void AdvancedRepairService_ConstructorWithNullLogger_ThrowsArgumentNullException()
    {
        var displayRefresh = new DisplayRefreshService(NullLogger<DisplayRefreshService>.Instance);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new AdvancedRepairService(null!, displayRefresh));

        Assert.Equal("logger", exception.ParamName);
    }

    [Fact]
    public void AdvancedRepairService_ConstructorWithNullDisplayRefresh_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new AdvancedRepairService(NullLogger<AdvancedRepairService>.Instance, null!));

        Assert.Equal("displayRefreshService", exception.ParamName);
    }
}
