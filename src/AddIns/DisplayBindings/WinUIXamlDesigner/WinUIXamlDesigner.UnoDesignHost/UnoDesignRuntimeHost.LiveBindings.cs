using System;
using System.IO;
using System.Text.Json;
using ICSharpCode.Core;
using ICSharpCode.WinUIXamlDesigner;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// Live design-time bindings for the Surface runtime: the renderer evaluates the app's <c>{x:Bind}</c> values against a
/// design-time page instance, and this is the switch that turns that on or off. Only the Surface child implements the
/// request; the Uno host reports <see cref="SupportsLiveBindings"/> false and the toolbar stays hidden.
/// </summary>
sealed partial class UnoDesignRuntimeHost : IWinUIXamlLiveBindings
{
	/// <summary>
	/// The user's choice for the session, shared by every document's host (one host is created per document). Null until
	/// the user presses the toggle, so the environment default decides the first document.
	/// </summary>
	static bool? liveBindingsPreference;

	/// <summary>What this document's child currently does; starts from the environment default.</summary>
	bool liveBindings = ResolveDefault();

	/// <summary>Only the Surface child has a renderer that can evaluate bindings.</summary>
	public bool SupportsLiveBindings => hostDisplayName.Contains("Surface", StringComparison.OrdinalIgnoreCase);

	public bool IsLiveBindings => liveBindingsPreference ?? liveBindings;

	public string SetLiveBindings(bool enabled)
	{
		if (!SupportsLiveBindings)
			return JsonSerializer.Serialize(new { live = liveBindings, message = "this renderer does not evaluate bindings" });
		if (client == null)
			return JsonSerializer.Serialize(new { live = liveBindings, message = "the renderer is not started" });
		try {
			var reply = client.LiveBindingsAsync(enabled).GetAwaiter().GetResult();
			liveBindings = reply.Live;
			liveBindingsPreference = reply.Live;
			StorePreference(reply.Live);
			return JsonSerializer.Serialize(new { live = liveBindings, message = reply.Message });
		} catch (Exception e) {
			LoggingService.Warn("WinUI live bindings failed", e);
			return JsonSerializer.Serialize(new { live = liveBindings, message = "the renderer was lost (" + e.GetBaseException().Message + ")" });
		}
	}

	/// <summary>
	/// The mode a new document's child starts in: the environment variable wins, then the file the toolbar toggle writes.
	/// The child resolves the same file on its side (<c>SurfaceDesignDocument.ResolveLiveBindings</c>), which is
	/// what makes the choice survive a new document, a restarted child and an IDE restart.
	/// </summary>
	static bool ResolveDefault() => SurfaceHost.SurfaceDesignDocument.ResolveLiveBindings();

	/// <summary>Records the user's choice where the child reads it. Best effort: the open document already changed.</summary>
	static void StorePreference(bool enabled)
	{
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(SurfaceHost.SurfaceDesignDocument.LiveBindingsPreferenceFile)!);
			File.WriteAllText(SurfaceHost.SurfaceDesignDocument.LiveBindingsPreferenceFile, enabled ? "on" : "off");
		} catch (Exception e) {
			LoggingService.Warn("WinUI live bindings preference could not be stored", e);
		}
	}
}
