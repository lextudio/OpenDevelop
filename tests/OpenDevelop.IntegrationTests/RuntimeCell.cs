using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The runtimes a designer test can target (see doc/technotes/integration-testing.md, "Platform and runtime matrix").
/// A cell is a runtime, not an OS: running on Windows says nothing about running against Microsoft WPF. A test declares
/// its cell with <c>[Trait(RuntimeCell.TraitName, RuntimeCell.X)]</c> so a run can be selected per cell
/// (<c>-trait "Runtime=LibreWpf"</c>), and asserts the backend the app actually reports with
/// <see cref="AssertWpfBackend"/>, so a test that silently ran on another backend fails instead of passing.
/// </summary>
public static class RuntimeCell
{
    public const string TraitName = "Runtime";

    public const string LibreWpf = "LibreWpf";
    public const string MicrosoftWpf = "MicrosoftWpf";

    // The cells below are named here so the traits can be applied as each designer's tests are moved over.
    public const string LibreWinForms = "LibreWinForms";
    public const string MicrosoftWinForms = "MicrosoftWinForms";
    public const string MicrosoftWinUI = "MicrosoftWinUI";
    public const string Uno = "Uno";
    public const string ProGpuWinUI = "ProGpuWinUI";

    /// <summary>The <c>backend</c> field <c>od.wpf-designer.status</c> reports for a WPF cell.</summary>
    public static string WpfBackendName(string cell) => cell switch {
        LibreWpf => "LibreWPF",
        MicrosoftWpf => "WPF",
        _ => throw new ArgumentException("Not a WPF cell: " + cell, nameof(cell)),
    };

    /// <summary>Fails when the WPF designer is served by a different backend than the test's cell.</summary>
    public static void AssertWpfBackend(JsonElement status, string cell)
    {
        Assert.True(status.TryGetProperty("backend", out var backend),
            "The WPF designer status did not report a backend: " + status);
        Assert.True(WpfBackendName(cell) == backend.GetString(),
            $"Expected the {cell} backend ('{WpfBackendName(cell)}') but the designer reported '{backend.GetString()}'. Status: {status}");
    }
}
