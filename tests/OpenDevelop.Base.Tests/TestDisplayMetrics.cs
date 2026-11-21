using ProGPU.Wpf.Interop;
using Xunit;

[assembly: AssemblyFixture(typeof(OpenDevelop.Base.Tests.TestDisplayMetrics))]

namespace OpenDevelop.Base.Tests;

/// <summary>
/// WPF controls read the primary screen size from SystemParameters (a ComboBox does so in its static
/// constructor). Under LibreWPF that is answered by the windowing backend's monitor service, which
/// initializes GLFW; on macOS GLFW only works on the main thread, while xUnit runs tests on other
/// threads, so the first such control (any DesignerCanvas) deadlocked the whole test process.
/// Unit tests do not need a real display, so every non-Windows run gets a fixed one.
/// This is an assembly fixture rather than a module initializer because the SDK's own bootstrap registers
/// its display source in a module initializer too, and the later registration wins: a fixture runs after
/// every initializer and before the first test.
/// </summary>
public sealed class TestDisplayMetrics : IDisposable
{
	readonly IDisposable? registration;

	public TestDisplayMetrics()
	{
		if (!OperatingSystem.IsWindows())
			registration = PortableWpfServiceRegistry.RegisterDisplayMetricsSource(new FixedDisplay());
	}

	public void Dispose() => registration?.Dispose();

	sealed class FixedDisplay : IPortableDisplayMetricsSource
	{
		public PortableWpfServiceKey ServiceKey => PortableWpfServiceKey.PresentationFramework;

		public event EventHandler? DisplayMetricsChanged { add { } remove { } }

		public bool TryGetDisplayMetrics(out PortableDisplayMetrics metrics)
		{
			var screen = new PortableRect(0, 0, 1920, 1080);
			metrics = new PortableDisplayMetrics(screen, screen, screen);
			return true;
		}
	}
}
