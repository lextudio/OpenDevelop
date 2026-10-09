using System.Reflection;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Project.HotReload;
using ICSharpCode.WinUIXamlDesigner.HotReload;
using ICSharpCode.WpfDesign.AddIn.HotReload;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Hot Reload is offered only where the application's runtime can run: Microsoft WPF and Windows
/// App SDK WinUI applications only run on Windows, LibreWPF runs everywhere. The adapters decide
/// from the project file alone (XamlFrameworkDetector), so a minimal project file per runtime is
/// enough - no IDE, no build.
/// </summary>
public class HotReloadAdapterPlatformTests : IDisposable
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "HotReloadAdapters-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    const string WindowsOnly = "only run on Windows";

    [Fact]
    public void MicrosoftWpf_IsOfferedOnlyOnWindows()
    {
        var context = Context("MsWpf", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");

        var handled = new WpfApplicationHotReloadAdapter().CanHandle(context, out var diagnostic);

        if (OperatingSystem.IsWindows()) {
            Assert.DoesNotContain(WindowsOnly, diagnostic ?? "");
        } else {
            Assert.False(handled);
            Assert.Contains(WindowsOnly, diagnostic);
        }
    }

    [Fact]
    public void WinUI_IsOfferedOnlyOnWindows()
    {
        var context = Context("WinUI", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><UseWinUI>true</UseWinUI></PropertyGroup></Project>");

        var handled = new WinUIApplicationHotReloadAdapter().CanHandle(context, out var diagnostic);

        if (OperatingSystem.IsWindows()) {
            Assert.DoesNotContain(WindowsOnly, diagnostic ?? "");
        } else {
            Assert.False(handled);
            Assert.Contains(WindowsOnly, diagnostic);
        }
    }

    [Fact]
    public void LibreWpf_IsNeverRejectedForThePlatform()
    {
        var context = Context("LibreWpf", "<Project Sdk=\"LibreWPF.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var handled = new WpfApplicationHotReloadAdapter().CanHandle(context, out var diagnostic);

        // It may still be refused for another reason - this test run may not have the agent
        // deployed next to the addin - but never because of the operating system.
        Assert.True(handled || !(diagnostic ?? "").Contains(WindowsOnly), diagnostic);
        Assert.DoesNotContain("not a WPF project", diagnostic ?? "");
    }

    HotReloadLaunchContext Context(string name, string projectXml)
    {
        var file = Path.Combine(_dir, name, name + ".csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, projectXml);
        return new HotReloadLaunchContext(ProjectStub.Create(FileName.Create(file)), withDebugger: false);
    }

    /// <summary>An IProject that knows its file name and nothing else - all an adapter's CanHandle reads.</summary>
    public class ProjectStub : DispatchProxy
    {
        FileName? fileName;

        public static IProject Create(FileName fileName)
        {
            var project = DispatchProxy.Create<IProject, ProjectStub>();
            ((ProjectStub)(object)project).fileName = fileName;
            return project;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_FileName")
                return fileName;
            var returnType = method?.ReturnType;
            return returnType == null || returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
        }
    }
}
