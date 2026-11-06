using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Issue #23: without a usable .NET SDK the IDE used to start and then fail the first project load
/// with MSBuild's "The SDK 'Microsoft.NET.Sdk' specified could not be found" and a stack. A .NET 10
/// SDK is a prerequisite, so the IDE must say so when it starts and leave instead.
///
/// <c>OD_IGNORE_DOTNET_SDKS</c> makes discovery return nothing, so these journeys run on a machine
/// that does have SDK 10 - the real state is otherwise only reachable by uninstalling it.
/// </summary>
[Collection("90 SDK selected at startup (isolated IDE)")]
public sealed class MissingSdkStartupIntegrationTests
{
    [Fact]
    public async Task Ide_ReportsTheMissingSdkAndExits_WhenNoSdkIsDiscovered()
    {
        string work = Path.Combine(Path.GetTempPath(), "opendevelop-no-sdk-" + Guid.NewGuid().ToString("N"));
        string configDir = Path.Combine(work, "config");
        Directory.CreateDirectory(configDir);
        try
        {
            var environment = new Dictionary<string, string> { ["OD_IGNORE_DOTNET_SDKS"] = "1" };
            var (exitCode, output) = await IsolatedIde.RunToExitAsync(configDir, environment, TimeSpan.FromMinutes(3));

            Assert.NotEqual(0, exitCode);
            // The dialog cannot be shown under OD_TEST_MODE, so the log is the record: it carries the
            // same text the Build pad and the dialog show.
            Assert.Contains("No usable .NET SDK was found for this process", output);
            Assert.Contains(".NET SDK 10 or newer: NOT FOUND", output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* a lingering handle; the temp folder is swept eventually */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The dialog is WPF, so it is found through the Windows window API: a visible top-level window
    /// of this process titled "Error" (the title ExceptionBox.ShowCopyableError gives it). Closing
    /// it must end the process - the dialog is the report, not an invitation to continue.
    /// </summary>
    [Fact]
    public async Task Ide_ShowsTheMissingSdkDialogAndExits_WhenNoSdkIsDiscovered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The dialog is located through the Windows window API.");

        string work = Path.Combine(Path.GetTempPath(), "opendevelop-no-sdk-dialog-" + Guid.NewGuid().ToString("N"));
        string configDir = Path.Combine(work, "config");
        Directory.CreateDirectory(configDir);

        // No test mode: OD_TEST_MODE suppresses every dialog, which is what the other journeys rely on.
        var environment = new Dictionary<string, string> { ["OD_IGNORE_DOTNET_SDKS"] = "1" };
        var (process, readOutput) = IsolatedIde.StartProcess(configDir, environment, testMode: false);
        try
        {
            IntPtr dialog = IntPtr.Zero;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (dialog == IntPtr.Zero && DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                    Assert.Fail($"The IDE exited (code {process.ExitCode}) without showing the dialog.\n{readOutput()}");
                dialog = FindWindow(process.Id, "Error");
                if (dialog == IntPtr.Zero)
                    await Task.Delay(500);
            }
            Assert.True(dialog != IntPtr.Zero,
                "No 'Error' window appeared on a machine with no usable .NET SDK.\n" + readOutput());

            Assert.True(PostMessage(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero), "Could not close the dialog window.");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"The IDE did not exit after its dialog was closed.\n{readOutput()}");
            }
            Assert.NotEqual(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            process.Dispose();
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* a lingering handle; the temp folder is swept eventually */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    static IntPtr FindWindow(int processId, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
                return true;
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != (uint)processId)
                return true;
            var text = new StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            if (!string.Equals(text.ToString(), title, StringComparison.Ordinal))
                return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    const uint WM_CLOSE = 0x0010;

    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
