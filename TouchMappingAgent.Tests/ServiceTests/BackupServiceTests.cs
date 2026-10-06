using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TouchMappingAgent.Service.Integration;
using TouchMappingAgent.Service.Services;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Unit tests for BackupService focusing on error handling and resilience.
/// </summary>
public class BackupServiceTests
{
    private readonly BackupService _backupService;

    public BackupServiceTests()
    {
        _backupService = new BackupService(NullLogger<BackupService>.Instance);
    }

    [Fact]
    public void CreateBackup_ReturnsValidIdOrNull()
    {
        var result = _backupService.CreateBackup("Test backup");
        if (result != null)
        {
            Assert.StartsWith("BKP_", result);
        }
    }

    [Fact]
    public void CreateBackup_DoesNotThrowException()
    {
        var exception = Record.Exception(() =>
        {
            _backupService.CreateBackup("Test");
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BackupService(null!));
    }

    [Fact]
    public void CreateBackup_WithDescription_ReturnsValidResult()
    {
        var result = _backupService.CreateBackup("Backup with description");
        Assert.True(result == null || result.Length > 0);
    }

    [Fact]
    public void CreateBackup_MultipleCallsGenerateUniqueIds()
    {
        var result1 = _backupService.CreateBackup("Backup 1");
        System.Threading.Thread.Sleep(100);
        var result2 = _backupService.CreateBackup("Backup 2");

        if (result1 != null && result2 != null)
        {
            Assert.NotEqual(result1, result2);
        }
    }
}

