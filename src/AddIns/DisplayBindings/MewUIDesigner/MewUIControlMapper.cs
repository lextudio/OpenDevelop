// MewUI's control types mapped onto OpenDevelop's shared control concepts, so MewUI Toolbox and
// Document Outline rows get the same glyphs and groups as every other framework's.
//
// The mappings live here rather than in DesignerToolboxCatalog because they track MewUI, not
// OpenDevelop: a MewUI release that adds or renames a control is a one-line fix in the file that
// already talks about MewUI. Each framework's addin owns its own map for the same reason.
//
// MewUI supplies no per-control icons of its own, so these rows fall back to the shared concept
// glyphs - which is the point: a framework with neither icons nor categories still reads correctly.

using System;
using System.Collections.Generic;

using ICSharpCode.SharpDevelop.Designer.Presentation;

namespace ICSharpCode.MewUIDesigner
{
	/// <summary>MewUI control names to the shared concepts.</summary>
	sealed class MewUIControlMapper : IDesignerControlMapper
	{
		public static readonly MewUIControlMapper Instance = new();

		MewUIControlMapper() { }

		public string FrameworkName => "MewUI";

		static readonly Dictionary<string, string> concepts = new(StringComparer.Ordinal) {
			// Containers. MewUI names them after the WPF controls it mirrors, so these are almost
			// identity - which is the point of the concept layer: when a framework happens to agree
			// with the shared vocabulary, its mapper is short.
			{ "StackPanel", "Stack" },
			{ "Grid", "Grid" },
			{ "DockPanel", "Dock" },
			{ "WrapPanel", "Stack" },
			{ "Border", "Border" },
			{ "ScrollViewer", "Scroll" },
			{ "Canvas", "Canvas" },
			{ "Panel", "Container" },
			{ "Expander", "Expander" },
			{ "Popup", "Popup" },
			{ "ToolTip", "Popup" },
			{ "ContextMenu", "Popup" },

			// Text.
			{ "Label", "Label" },
			{ "TextBlock", "Label" },
			{ "TextBox", "Text Input" },
			{ "PasswordBox", "Password" },

			// Inputs.
			{ "Button", "Button" },
			{ "ToggleButton", "Toggle Button" },
			{ "CheckBox", "Check Box" },
			{ "RadioButton", "Radio Button" },
			{ "ComboBox", "Combo Box" },
			{ "ListBox", "List Box" },
			{ "ListView", "List View" },
			{ "TreeView", "Tree View" },
			{ "TabControl", "Tab" },
			{ "Slider", "Slider" },
			{ "NumericUpDown", "Numeric" },
			{ "AutoCompleteBox", "Auto Complete" },
			{ "DatePicker", "Calendar" },
			{ "Calendar", "Calendar" },
			{ "ColorPicker", "Expander Input" },

			// Progress and media.
			{ "ProgressBar", "Progress" },
			{ "ProgressRing", "Progress" },
			{ "Image", "Image" },
		};

		public string? GetConcept(string typeName)
		{
			return typeName != null && concepts.TryGetValue(typeName, out var concept) ? concept : null;
		}

		public string? GetCategory(string typeName)
		{
			// MewUI has no category concept of its own, so the shared one applies.
			return null;
		}
	}
}
