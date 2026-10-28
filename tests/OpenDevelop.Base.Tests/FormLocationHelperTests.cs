using System.Windows;
using ICSharpCode.SharpDevelop.Gui;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// <see cref="FormLocationHelper.Validate(Rect, Rect)"/> must hand back bounds that lie inside the work area. A window
/// that only overhangs it used to be returned unchanged, and macOS then reshaped the native window (menu bar, Dock) so
/// that the position and size WPF reported no longer matched the screen. The work area is passed in explicitly:
/// SystemParameters.WorkArea would initialise GLFW, which blocks off the main thread on macOS.
/// </summary>
public sealed class FormLocationHelperTests
{
	// The hosted macOS runner: a 1024x768 screen whose menu bar (25) and Dock (62) leave 1024x681.
	static readonly Rect WorkArea = new(0, 25, 1024, 681);

	static void AssertInside(Rect bounds)
	{
		Assert.True(bounds.Left >= WorkArea.Left && bounds.Top >= WorkArea.Top
			&& bounds.Right <= WorkArea.Right && bounds.Bottom <= WorkArea.Bottom,
			$"{bounds} is not inside the work area {WorkArea}");
	}

	[Fact]
	public void Validate_FitsTheWorkbenchOfTheHostedMacOSRunnerToItsWorkArea()
	{
		// What the workbench asks for: 1024x768 at (10,10).
		var result = FormLocationHelper.Validate(new Rect(10, 10, 1024, 768), WorkArea);
		Assert.Equal(WorkArea, result);
	}

	[Fact]
	public void Validate_SlidesAnOverhangingWindowInsideWithoutResizingIt()
	{
		var result = FormLocationHelper.Validate(new Rect(900, 600, 400, 300), WorkArea);
		AssertInside(result);
		Assert.Equal(400, result.Width);
		Assert.Equal(300, result.Height);
		Assert.Equal(new Rect(624, 406, 400, 300), result);
	}

	[Fact]
	public void Validate_LeavesAWindowThatAlreadyFitsAlone()
	{
		var bounds = new Rect(100, 120, 500, 400);
		Assert.Equal(bounds, FormLocationHelper.Validate(bounds, WorkArea));
	}

	[Fact]
	public void Validate_MovesAWindowAboveTheWorkAreaBelowTheMenuBar()
	{
		var result = FormLocationHelper.Validate(new Rect(50, 0, 300, 200), WorkArea);
		Assert.Equal(new Rect(50, 25, 300, 200), result);
	}

	[Fact]
	public void Validate_CentersAWindowThatIsOffTheScreenEntirely()
	{
		var result = FormLocationHelper.Validate(new Rect(5000, 5000, 200, 100), WorkArea);
		AssertInside(result);
		Assert.Equal(new Rect(412, 315.5, 200, 100), result);
	}
}
