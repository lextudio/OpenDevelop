using System;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>Normalizes what a strip/menu "Type Here" insertion cell should do with committed
	/// text, shared groundwork for <c>RemoteFormsDesignerControl.CommitTypeHere</c> (WinForms
	/// MenuStrip/ToolStrip/StatusStrip) and the WPF Menu/ContextMenu "Type Here" slot - both
	/// designers show one trailing editable cell after the last real item, typed text becomes a new
	/// sibling item's display text, and an empty commit or an explicit Cancel is a no-op. Deliberately
	/// just this one decision, not a stateful controller: whether the slot is currently visible/being
	/// edited is easier to track as ordinary fields alongside each designer's own text-editor state
	/// (as the WPF side already does for its existing inline Header editor) than behind a shared type
	/// with no UI-framework vocabulary of its own to describe. WinForms' own CommitTypeHere is not
	/// changed to call this in this pass - see wpf-designer.md for why that's a separate, riskier
	/// change - but its shape matches this exactly and could be pointed at it later.</summary>
	public static class StripTypeHereCommit
	{
		/// <summary>The header/text a new sibling item should get, or null if nothing should be
		/// created - either because the user cancelled or left the cell empty.</summary>
		public static string? Resolve(string? typedText, bool cancelled)
		{
			if (cancelled)
				return null;
			var trimmed = typedText?.Trim();
			return String.IsNullOrEmpty(trimmed) ? null : trimmed;
		}
	}
}
