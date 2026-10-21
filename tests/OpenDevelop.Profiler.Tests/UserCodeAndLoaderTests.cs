using OpenDevelop.Profiler.Cpu;
using Xunit;

namespace OpenDevelop.Profiler.Tests;

public class UserCodeTests
{
    static FrameInfo Frame(string module) => new("M()", module);

    [Theory]
    [InlineData("MyApp", true)]
    [InlineData("myapp", true)] // module names are case-insensitive
    [InlineData("MyApp.dll", true)]
    [InlineData("System.Private.CoreLib", false)]
    public void FromAssemblies_MatchesTheSolutionsAssemblies(string module, bool expected)
    {
        var isUser = UserCode.FromAssemblies(["MyApp.exe"]);

        Assert.Equal(expected, isUser(Frame(module)));
    }

    static CpuProfile ProfileOf(params string[] modules) => new(
        modules.Select(m => new FrameInfo("M()", m)).ToList(),
        [new CpuSample(0, 1, SampleState.Managed, Enumerable.Range(0, modules.Length).ToArray())],
        0);

    [Fact]
    public void ForProfile_UsesTheSolutionsAssembliesWhenTheProfileContainsThem()
    {
        var profile = ProfileOf("System.Private.CoreLib", "MyApp", "Vendor.Lib");

        var isUser = UserCode.ForProfile(profile, ["MyApp"]);

        Assert.True(isUser(profile.Frames[1]));
        // Only the solution counts as "mine"; a third-party library is not, even though it is not the framework.
        Assert.False(isUser(profile.Frames[2]));
    }

    [Fact]
    public void ForProfile_FallsBackToTheFrameworkHeuristicForAProcessOutsideTheSolution()
    {
        var profile = ProfileOf("System.Private.CoreLib", "SomeOtherApp", "ntdll");

        var isUser = UserCode.ForProfile(profile, ["MyApp"]);

        // Folding everything into [External Code] would leave nothing to look at.
        Assert.True(isUser(profile.Frames[1]));
        Assert.False(isUser(profile.Frames[0]));
        Assert.False(isUser(profile.Frames[2]));
    }

    [Fact]
    public void ForProfile_WithNoSolutionUsesTheHeuristic()
    {
        var profile = ProfileOf("System.Linq", "SomeOtherApp");

        var isUser = UserCode.ForProfile(profile, []);

        Assert.True(isUser(profile.Frames[1]));
        Assert.False(isUser(profile.Frames[0]));
    }

    [Theory]
    [InlineData("MyApp", true)]
    [InlineData("Systematic.Tools", true)] // starts with "System" but is not a System assembly
    [InlineData("MicrosoftlikeLib", true)]
    [InlineData("System", false)]
    [InlineData("System.Private.CoreLib", false)]
    [InlineData("Microsoft.Extensions.Logging", false)]
    [InlineData("PresentationFramework", false)]
    [InlineData("coreclr", false)]
    [InlineData("ntdll", false)]
    [InlineData("", false)]
    [InlineData("UNKNOWN", false)]
    public void NotFramework_SeparatesUserCodeFromTheRuntimeAndFrameworks(string module, bool expected) =>
        Assert.Equal(expected, UserCode.NotFramework(Frame(module)));
}
