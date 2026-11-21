using System.Collections.Generic;
using ICSharpCode.SharpDevelop.Widgets;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The shared toolbar's native-live toggle: it exists only for a backend that opts in, a user press
/// is the only thing that asks the backend to change mode, and reflecting a backend-initiated change
/// never echoes a second request (so a failed start can visibly uncheck the button).
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class DesignerCanvasNativeLiveTests
{
	[Xunit.UIFactAttribute]
	public void The_toggle_is_absent_until_the_backend_opts_in()
	{
		var canvas = new DesignerCanvas();

		Assert.DoesNotContain("Native Live", canvas.VisibleToolbarItems);
		Assert.False(canvas.RequestNativeLive(true));

		canvas.ShowNativeLive = true;

		Assert.Contains("Native Live", canvas.VisibleToolbarItems);
	}

	[Xunit.UIFactAttribute]
	public void A_press_asks_the_backend_and_a_backend_change_does_not_echo_it()
	{
		var canvas = new DesignerCanvas { ShowNativeLive = true };
		var requests = new List<bool>();
		canvas.NativeLiveRequested += (_, on) => requests.Add(on);

		// The user presses the toggle: the backend is asked, and the button shows the attempt.
		Assert.True(canvas.RequestNativeLive(true));
		Assert.True(canvas.IsNativeLive);
		Assert.Equal(new[] { true }, requests);

		// The backend could not start the mode and reports so: the button unchecks without asking again.
		canvas.SetNativeLive(false);
		Assert.False(canvas.IsNativeLive);
		Assert.Equal(new[] { true }, requests);

		// Pressing again asks again.
		Assert.True(canvas.RequestNativeLive(true));
		Assert.Equal(new[] { true, true }, requests);
	}

	[Xunit.UIFactAttribute]
	public void Reflecting_a_change_made_elsewhere_does_not_ask_the_backend()
	{
		var canvas = new DesignerCanvas { ShowNativeLive = true };
		var requests = new List<bool>();
		canvas.NativeLiveRequested += (_, on) => requests.Add(on);

		// A DevFlow action or the backend itself entered native mode.
		canvas.SetNativeLive(true);

		Assert.True(canvas.IsNativeLive);
		Assert.Empty(requests);

		canvas.SetNativeLive(false);
		Assert.False(canvas.IsNativeLive);
		Assert.Empty(requests);
	}
}
