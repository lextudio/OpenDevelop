using System;
using System.Text.Json;
using ICSharpCode.Core;
using ICSharpCode.WinUIXamlDesigner;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// Live design-time bindings for the Surface runtime: the renderer evaluates the app's <c>{x:Bind}</c> values against a
/// design-time page instance, and this is the switch that turns that on or off per document. Only the Surface child
/// implements the request; the Uno host reports <see cref="SupportsLiveBindings"/> false and the toolbar stays hidden.
/// </summary>
sealed partial class UnoDesignRuntimeHost : IWinUIXamlLiveBindings
{
	bool liveBindings = Environment.GetEnvironmentVariable("OD_WINUI_LIVE_BINDINGS") != "0";

	/// <summary>Only the Surface child has a renderer that can evaluate bindings.</summary>
	public bool SupportsLiveBindings => hostDisplayName.Contains("Surface", StringComparison.OrdinalIgnoreCase);

	public bool IsLiveBindings => liveBindings;

	public string SetLiveBindings(bool enabled)
	{
		if (!SupportsLiveBindings)
			return JsonSerializer.Serialize(new { live = liveBindings, message = "this renderer does not evaluate bindings" });
		if (client == null)
			return JsonSerializer.Serialize(new { live = liveBindings, message = "the renderer is not started" });
		try {
			var reply = client.LiveBindingsAsync(enabled).GetAwaiter().GetResult();
			liveBindings = reply.Live;
			return JsonSerializer.Serialize(new { live = liveBindings, message = reply.Message });
		} catch (Exception e) {
			LoggingService.Warn("WinUI live bindings failed", e);
			return JsonSerializer.Serialize(new { live = liveBindings, message = "the renderer was lost (" + e.GetBaseException().Message + ")" });
		}
	}
}
