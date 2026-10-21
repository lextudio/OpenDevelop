using System.Threading;
using ICSharpCode.SharpDevelop.Widgets;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class DesignerCanvasUnavailableTests
{
	/// <summary>
	/// Windows only because constructing a WPF <see cref="ContentControl"/> needs an STA thread,
	/// which only Windows provides. The condition the test exercises is not Windows-specific: it is
	/// the macOS case (a Microsoft backend that cannot run here) that motivated it.
	/// </summary>
	public static bool StaThreadAvailable => OperatingSystem.IsWindows();

	/// <summary>
	/// <c>ShowUnavailable</c> is the single place every designer explains "this document cannot be
	/// designed here" - Microsoft WPF and Microsoft WinForms on macOS, Microsoft WinUI on anything
	/// but Windows. It must leave exactly ONE copy of that sentence on screen: the loading overlay.
	///
	/// It used to mirror the same text into the canvas status bar too. The status bar is its own
	/// layout row (below the overlay's row), so that second copy stayed visible underneath the
	/// overlay - reported as "two duplicate messages" for WPF and WinForms, and as three for WinUI,
	/// whose view content additionally mirrors its host's status into its own status line.
	///
	/// The status bar collapses while its text is empty, so an empty <c>StatusText</c> is exactly
	/// "no second copy" - whatever StatusBar capability a backend enabled - which is why this
	/// asserts the text rather than the capability: clearing the capability would also strip the
	/// status bar from a later normal load that reuses the same canvas.
	/// </summary>
	[Fact(Skip = "WPF controls need an STA thread, which only Windows provides.", SkipUnless = nameof(StaThreadAvailable))]
	public void ShowUnavailable_LeavesTheStatusBarEmpty_SoTheOverlayIsTheOnlyCopy()
	{
		RunOnStaThread(() => {
			var canvas = new DesignerCanvas();

			canvas.ShowUnavailable("This project uses Microsoft WinForms, which runs only on Windows.");

			Assert.True(canvas.IsUnavailable);
			Assert.Equal(string.Empty, canvas.StatusText);
		});
	}

	/// <summary>
	/// A WPF control must be created on the STA thread that owns its dispatcher; the shared test
	/// host runs tests on thread-pool threads (which is also why SharedToolboxTests cannot touch
	/// these controls). Host the action on its own STA thread and rethrow whatever it produced, so
	/// a failed assertion still fails the test rather than being swallowed by the thread.
	/// </summary>
	static void RunOnStaThread(Action action)
	{
		Exception? failure = null;
		var thread = new Thread(() => {
			try {
				action();
			} catch (Exception ex) {
				failure = ex;
			}
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		thread.Join();
		if (failure != null)
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
	}
}
