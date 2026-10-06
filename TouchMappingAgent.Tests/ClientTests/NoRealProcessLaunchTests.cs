using System.Reflection;
using TouchMappingAgent.WPF.Services;
using TouchMappingAgent.WPF.ViewModels;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Stops the test suite from ever again launching a real process on the developer's desktop.
///
/// WHAT HAPPENED: SetupWizardViewModel and ReapplyAgent each called Process.Start directly.
/// SetupWizardTests supplied the genuine C:\Windows\System32\tabcal.exe together with the
/// placeholder arguments "LinCal ...", so every run of the suite started the real calibration
/// tool, which answers a malformed command line with a modal "Ungültige Befehlszeilensyntax"
/// dialog — on whatever screen the developer was working on, repeatedly, with no indication that
/// the test suite was the cause.
///
/// The structural fix was <see cref="ITabcalRunner"/>. This test defends that fix: it fails the
/// moment a class that runs calibration reacquires its own Process.Start, which is exactly how
/// the problem would come back.
/// </summary>
public class NoRealProcessLaunchTests
{
    /// <summary>
    /// The classes a test constructs and drives. TabcalRunner is deliberately absent: it is the
    /// one place allowed to start the process, and no test constructs it.
    /// </summary>
    public static TheoryData<Type> ClassesTestsDrive
    {
        get
        {
            var data = new TheoryData<Type>();
            data.Add(typeof(SetupWizardViewModel));
            data.Add(typeof(ReapplyAgent));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ClassesTestsDrive))]
    public void Classes_under_test_do_not_start_processes_themselves(Type type)
    {
        // System.Diagnostics.Process anywhere in the field or parameter surface of the type means
        // it is handling processes itself rather than delegating to ITabcalRunner.
        var offendingMembers = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.DeclaringType == type)
            .Where(m => m.ReturnType.FullName?.Contains("System.Diagnostics.Process") == true
                        || m.GetParameters().Any(p =>
                            p.ParameterType.FullName?.Contains("System.Diagnostics.Process") == true))
            .Select(m => m.Name)
            .ToList();

        Assert.True(offendingMembers.Count == 0,
            $"{type.Name} handles System.Diagnostics.Process directly ({string.Join(", ", offendingMembers)}). " +
            "Calibration must go through ITabcalRunner so tests cannot start the real tabcal.exe.");
    }

    [Theory]
    [MemberData(nameof(ClassesTestsDrive))]
    public void Classes_under_test_take_the_runner_as_a_dependency(Type type)
    {
        // The positive half of the same rule: delegation is only guaranteed if the collaborator is
        // injected. A class that resolved its own runner internally could still be given the real
        // one by a test.
        var takesRunner = type
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ITabcalRunner)));

        Assert.True(takesRunner,
            $"{type.Name} must accept an ITabcalRunner so a test can supply a fake.");
    }

    [Fact]
    public async Task The_fake_records_invocations_without_starting_anything()
    {
        // Proves the fake is a real substitute: a test can assert on the command line that WOULD
        // have run, which is what the removed Process.Start code made impossible.
        var runner = new FakeTabcalRunner();

        var result = await runner.RunAsync(
            @"C:\Windows\System32\tabcal.exe",
            "LinCal DisplayID=\\\\.\\DISPLAY1 DeviceKind=touch NoValidate");

        Assert.True(result.Success);
        Assert.Single(runner.Invocations);
        Assert.Equal(@"C:\Windows\System32\tabcal.exe", runner.Invocations[0].ExecutablePath);
        Assert.Contains("LinCal", runner.Invocations[0].Arguments);
    }
}
