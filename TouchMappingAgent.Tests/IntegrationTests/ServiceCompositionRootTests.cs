using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TouchMappingAgent.Service.HealthMonitoring;
using TouchMappingAgent.Service.Hardware;
using TouchMappingAgent.Service.IPC;
using TouchMappingAgent.Service.Integration;
using TouchMappingAgent.Service.Native;
using TouchMappingAgent.Service.Services;
using Xunit;

namespace TouchMappingAgent.Tests.IntegrationTests;

/// <summary>
/// DI regression tests for the service composition root.
///
/// These exist because of a defect that every other test in this suite missed: the service
/// used to declare three separate, near-identical project-owned ILogger interfaces (one each
/// in the Hardware, Integration and Services namespaces) while Program.cs registered only
/// one of them. BackupService, DisplayRefreshService and AdvancedRepairService all depended
/// on an interface that was never registered, so ComplianceRequestHandler could not be
/// constructed at runtime at all.
///
/// Nothing caught it: the unit tests build their subjects by hand with mocks, bypassing the
/// container entirely, and NamedPipeServer resolves the handler inside a try/catch that
/// turned the InvalidOperationException into a generic "operation failed" response. Every
/// MapTouch, CreateBackup and AdvancedRepair request failed in the field while the health
/// check (GetTouchDevices — no DI involved) still reported the service as healthy, and the
/// suite stayed green at 32/32.
///
/// The rule these tests enforce: the composition root must be exercised as a whole, and
/// every service registered in it must actually be constructible.
/// </summary>
public class ServiceCompositionRootTests
{
    /// <summary>
    /// Mirrors the ConfigureServices block of
    /// <c>TouchMappingAgent.Service.Program.Main</c>. Logging is supplied here the way the
    /// generic host supplies it in production (ILogger&lt;T&gt; via AddLogging), so a service
    /// that reintroduces a bespoke logger abstraction fails these tests immediately.
    /// </summary>
    private static ServiceCollection BuildProductionServiceCollection()
    {
        var services = new ServiceCollection();

        // Provided by Host.CreateDefaultBuilder(...).UseSerilog() in production.
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(NullLoggerProvider.Instance);
        });

        services.AddTransient<DisplayRefreshService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<AdvancedRepairService>();
        services.AddSingleton<MappingStore>();
        services.AddSingleton<ReapplyCoordinator>();
        services.AddSingleton<ResilientHardwareWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ResilientHardwareWatcher>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<NativeWtsSessionProvider>();
        services.AddSingleton<IWtsSessionProvider>(sp => new TimeBoundedWtsSessionProvider(
            sp.GetRequiredService<NativeWtsSessionProvider>(),
            sp.GetRequiredService<ILogger<TimeBoundedWtsSessionProvider>>()));
        services.AddSingleton<ConsoleSessionResetService>();
        services.AddSingleton<ISessionProcessService, SessionProcessService>();
        services.AddSingleton<ConsoleDisplayHeartbeatState>();
        services.AddSingleton<ConsoleSessionWatchdog>();
        services.AddHostedService(sp => sp.GetRequiredService<ConsoleSessionWatchdog>());
        services.AddSingleton<Evolved.EdidManager.Registry.IEdidRegistryStore,
            Evolved.EdidManager.Registry.EdidRegistryStore>();
        services.AddSingleton<Evolved.EdidManager.Pnp.IPnpDeviceControl,
            Evolved.EdidManager.Pnp.PnpDeviceControl>();
        services.AddSingleton<Evolved.EdidManager.IElevationCheck,
            Evolved.EdidManager.WindowsElevationCheck>();
        services.AddSingleton<Evolved.EdidManager.Templates.IEdidTemplateStore,
            Evolved.EdidManager.Templates.EdidTemplateStore>();
        services.AddSingleton<Evolved.EdidManager.EdidManagerService>();
        services.AddSingleton<ComplianceRequestHandler>();
        services.AddSingleton<NamedPipeServer>();

        return services;
    }

    /// <summary>
    /// Builds the container with the same validation flags Program.cs uses. ValidateOnBuild
    /// walks every registration and throws on the first unsatisfiable constructor — this is
    /// the check that would have caught the original defect at startup.
    /// </summary>
    [Fact]
    public void CompositionRoot_BuildsWithValidationEnabled()
    {
        var services = BuildProductionServiceCollection();

        var exception = Record.Exception(() => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        }));

        Assert.Null(exception);
    }

    /// <summary>
    /// Resolves every registered service type. ValidateOnBuild already covers this, but an
    /// explicit resolution proves the instances can really be constructed (and names the
    /// offending type in the assertion message if one cannot).
    /// </summary>
    [Fact]
    public void CompositionRoot_ResolvesEveryRegisteredService()
    {
        var services = BuildProductionServiceCollection();
        var registeredTypes = services
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => !type.IsGenericTypeDefinition)
            .Distinct()
            .ToList();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var failures = new List<string>();

        foreach (var serviceType in registeredTypes)
        {
            try
            {
                var instance = provider.GetService(serviceType);
                if (instance == null)
                    failures.Add($"{serviceType.FullName}: resolved to null");
            }
            catch (Exception ex)
            {
                failures.Add($"{serviceType.FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            "The following registered services could not be resolved:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The specific regression: ComplianceRequestHandler is the type that silently failed to
    /// resolve, and it is the one every functional IPC command (MapTouch, ConfirmLocalMapping,
    /// CreateBackup, AdvancedRepair) goes through.
    /// </summary>
    [Fact]
    public void CompositionRoot_ResolvesComplianceRequestHandler()
    {
        using var provider = BuildProductionServiceCollection().BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var handler = provider.GetService<ComplianceRequestHandler>();

        Assert.NotNull(handler);
    }

    /// <summary>
    /// Reproduces the exact failure path from the field: NamedPipeServer asks the container
    /// for the handler and, before the fix, got an exception it then swallowed. Going through
    /// GetService the same way the pipe server does proves the resolution really works rather
    /// than only the direct construction.
    /// </summary>
    [Fact]
    public void CompositionRoot_ServiceProviderResolvesHandlerTheWayNamedPipeServerDoes()
    {
        using var provider = BuildProductionServiceCollection().BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var handler = provider.GetService(typeof(ComplianceRequestHandler)) as ComplianceRequestHandler;

        Assert.NotNull(handler);
    }

    /// <summary>
    /// Guards the consolidation itself: the service assembly must not declare a project-owned
    /// ILogger abstraction again. Reintroducing one is how the original defect arose, and it
    /// is invisible to every behavioural test.
    /// </summary>
    [Fact]
    public void ServiceAssembly_DeclaresNoCustomLoggerAbstraction()
    {
        var serviceAssembly = typeof(ComplianceRequestHandler).Assembly;

        var customLoggerTypes = serviceAssembly
            .GetTypes()
            .Where(type => type.Name is "ILogger" or "ILogger`1")
            .Select(type => type.FullName)
            .ToList();

        Assert.True(customLoggerTypes.Count == 0,
            "The service must use Microsoft.Extensions.Logging.ILogger<T> exclusively. Found: " +
            string.Join(", ", customLoggerTypes));
    }

    /// <summary>
    /// Every service constructor that takes a logger must take the generic
    /// Microsoft.Extensions.Logging.ILogger&lt;T&gt; closed over its own type — that is what
    /// makes the registrations resolvable from the host-provided logging pipeline.
    /// </summary>
    [Fact]
    public void ServiceConstructors_TakeGenericMicrosoftLogger()
    {
        var typesUnderTest = new[]
        {
            typeof(BackupService),
            typeof(DisplayRefreshService),
            typeof(AdvancedRepairService),
            typeof(MappingStore),
            typeof(ReapplyCoordinator),
            typeof(ResilientHardwareWatcher),
            typeof(ComplianceRequestHandler),
            typeof(NamedPipeServer),
            typeof(ConsoleSessionResetService),
            typeof(ConsoleSessionWatchdog),
            typeof(SessionProcessService),
            typeof(TimeBoundedWtsSessionProvider)
        };

        var failures = new List<string>();

        foreach (var type in typesUnderTest)
        {
            var expected = typeof(ILogger<>).MakeGenericType(type);

            // EVERY public constructor is checked, not just one: MappingStore deliberately
            // exposes a second one taking a RegistryKey so tests can point it at HKCU, and a
            // bespoke logger could otherwise hide in an overload the container never picks.
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (constructors.Length == 0)
            {
                failures.Add($"{type.Name}: no public constructor");
                continue;
            }

            foreach (var constructor in constructors)
            {
                var loggerParameter = constructor
                    .GetParameters()
                    .FirstOrDefault(p => p.ParameterType.Name.StartsWith("ILogger", StringComparison.Ordinal));

                if (loggerParameter == null)
                {
                    failures.Add($"{type.Name}: a constructor takes no logger parameter");
                    continue;
                }

                if (loggerParameter.ParameterType != expected)
                {
                    failures.Add(
                        $"{type.Name}: expected {expected.Name} but found {loggerParameter.ParameterType.FullName}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            string.Join(Environment.NewLine, failures));
    }
}
