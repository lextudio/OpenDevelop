using System.Collections.Generic;
using ICSharpCode.SharpDevelop.Widgets;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The shared toolbar's live-bindings toggle: it exists only for a renderer that can evaluate bindings, a user press is
/// the only thing that asks the backend to change mode, and reflecting a backend-initiated change never echoes a second
/// request (so a renderer that stays on placeholders can visibly uncheck the button).
/// </summary>
public sealed class DesignerCanvasLiveBindingsTests
{
	[Xunit.UIFactAttribute]
	public void The_toggle_is_absent_until_the_backend_opts_in()
	{
		var canvas = new DesignerCanvas();

		Assert.DoesNotContain("Live Bindings", canvas.VisibleToolbarItems);
		Assert.False(canvas.RequestLiveBindings(true));

		canvas.ShowLiveBindings = true;

		Assert.Contains("Live Bindings", canvas.VisibleToolbarItems);
	}

	[Xunit.UIFactAttribute]
	public void A_press_asks_the_backend_and_a_backend_change_does_not_echo_it()
	{
		var canvas = new DesignerCanvas { ShowLiveBindings = true };
		var requests = new List<bool>();
		canvas.LiveBindingsRequested += (_, on) => requests.Add(on);

		// The toggle starts off; pressing it asks the backend and shows the attempt.
		Assert.True(canvas.RequestLiveBindings(true));
		Assert.True(canvas.IsLiveBindings);
		Assert.Equal(new[] { true }, requests);

		// The renderer could not change mode and reports so: the button returns without asking again.
		canvas.SetLiveBindings(false);
		Assert.False(canvas.IsLiveBindings);
		Assert.Equal(new[] { true }, requests);

		// A real press the other way asks again.
		Assert.True(canvas.RequestLiveBindings(true));
		Assert.Equal(new[] { true, true }, requests);
	}

	[Xunit.UIFactAttribute]
	public void Reflecting_a_change_made_elsewhere_does_not_ask_the_backend()
	{
		var canvas = new DesignerCanvas { ShowLiveBindings = true };
		var requests = new List<bool>();
		canvas.LiveBindingsRequested += (_, on) => requests.Add(on);

		// The default mode is on, and the view reflects it when it wires the toggle.
		canvas.SetLiveBindings(true);
		Assert.True(canvas.IsLiveBindings);

		// A DevFlow action turned it off.
		canvas.SetLiveBindings(false);
		Assert.False(canvas.IsLiveBindings);
		Assert.Empty(requests);
	}
}
