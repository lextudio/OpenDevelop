// Shared JSON-RPC wire configuration for the designer control plane, used by both the IDE-side
// client (DesignerHostProcessClient) and the child-side server (DesignerChildHost).

using System;
using System.Diagnostics;
using StreamJsonRpc;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// Builds the <see cref="SystemTextJsonFormatter"/> both ends of the designer protocol must
	/// use, so a change to one side's serialization behaviour cannot silently diverge from the
	/// other's.
	///
	/// The one setting that actually matters: <see cref="System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals"/>.
	/// Every geometry field this protocol carries (<c>DesignerElementNode.X/Y/Width/Height</c>,
	/// grid track offsets, ...) is a plain <c>double</c>, and WinUI/Uno layout genuinely produces
	/// NaN/Infinity for it - not as a bug in the layout, but as the normal value of
	/// ActualWidth/DesiredSize for an element mid-animation or not yet arranged (WinUI-Gallery's
	/// AnimatedIconPage reproduces it reliably: its icons drive their own composition-based layout
	/// independent of the framework's arrange pass). System.Text.Json's default double converter
	/// THROWS on those values rather than writing them, and StreamJsonRpc reports that as the
	/// maximally unhelpful "An error occured during serialization" with no field name, no page
	/// name, nothing to grep for - which is what made this look like a mystery specific to one
	/// page rather than a wire-format gap that any NaN geometry hits.
	/// </summary>
	public static class DesignerJsonRpc
	{
		public static SystemTextJsonFormatter CreateFormatter()
		{
			var formatter = new SystemTextJsonFormatter();
			formatter.JsonSerializerOptions.NumberHandling =
				System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
			return formatter;
		}

		/// <summary>
		/// Surfaces StreamJsonRpc's OWN diagnostic trace to stderr at Warning+.
		///
		/// A send/receive failure inside StreamJsonRpc - most usefully a serialization exception -
		/// reaches the RPC caller only as a fixed, contentless string ("An error occured during
		/// serialization.", StreamJsonRpc's own literal, typo included). The real exception (which
		/// field, which type, what value) is not attached as an InnerException; it only ever
		/// reaches <see cref="JsonRpc.TraceSource"/>, which nothing was listening to. Attach this
		/// right after constructing a <see cref="JsonRpc"/> (before StartListening) on both ends so
		/// that detail lands in the child's own stderr - which the IDE side already routes to the
		/// designer's Output pad channel (see DesignerOutput) - instead of being lost.
		/// </summary>
		public static void AttachDiagnosticTracing(JsonRpc rpc)
		{
			rpc.TraceSource.Switch.Level = SourceLevels.Warning;
			rpc.TraceSource.Listeners.Add(new TextWriterTraceListener(Console.Error));
		}
	}
}
