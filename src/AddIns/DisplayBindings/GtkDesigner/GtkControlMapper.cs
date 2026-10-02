// GTK's control types mapped onto OpenDevelop's shared control concepts, so that GTK Toolbox rows
// and Document Outline rows get the same glyphs as every other framework's without Core having to
// know that GTK spells its text box "Entry".
//
// The mappings live here rather than in DesignerToolboxCatalog because they change with GTK, not with
// OpenDevelop: a GTK 4 upgrade that renames a class is a one-line fix in the file that already talks
// about GTK. Each framework's addin owns its own map for the same reason.
//
// GTK supplies no per-control icons of its own, so these rows fall back to the shared concept
// glyphs - which is the whole point: a framework with neither icons nor categories still reads
// correctly.

using System;
using System.Collections.Generic;

using ICSharpCode.SharpDevelop.Designer.Presentation;

namespace ICSharpCode.GtkDesigner
{
	/// <summary>GtkWidget class names (already stripped of their "Gtk" prefix by the normalizer) to
	/// the shared concepts.</summary>
	sealed class GtkControlMapper : IDesignerControlMapper
	{
		public static readonly GtkControlMapper Instance = new();

		GtkControlMapper() { }

		public string FrameworkName => "GTK";

		static readonly Dictionary<string, string> concepts = new(StringComparer.Ordinal) {
			// Containers. GTK's Box is the StackPanel analogue; CenterBox centers one child.
			{ "Box", "Stack" },
			{ "Grid", "Grid" },
			{ "HBox", "Stack" },
			{ "VBox", "Stack" },
			{ "CenterBox", "Stack" },
			{ "Paned", "Dock" },
			{ "HPaned", "Dock" },
			{ "VPaned", "Dock" },
			{ "ScrolledWindow", "Scroll" },
			{ "Overlay", "Container" },
			{ "Revealer", "Expander" },
			{ "Expander", "Expander" },
			{ "Notebook", "Tab" },
			{ "Stack", "Stack" },
			{ "ViewPort", "Canvas" },

			// Text. GtkEntry is the TextBox, which is exactly the case the shared concept exists for.
			{ "Label", "Label" },
			{ "Entry", "Text Input" },
			{ "Text", "Multiline Text" },
			{ "TextView", "Multiline Text" },
			{ "PasswordEntry", "Password" },

			// Inputs. GtkSwitch is a toggle, GtkDropDown is the ComboBox, GtkSpinButton the NumericUpDown.
			{ "Button", "Button" },
			{ "CheckButton", "Check Box" },
			{ "RadioButton", "Radio Button" },
			{ "Switch", "Toggle Button" },
			{ "ToggleButton", "Toggle Button" },
			{ "MenuButton", "Drop-down Button" },
			{ "DropDown", "Combo Box" },
			{ "ComboBoxText", "Combo Box" },
			{ "SpinButton", "Numeric" },
			{ "Scale", "Slider" },
			{ "ProgressBar", "Progress" },
			{ "ListBox", "List Box" },
			{ "ListView", "List View" },
			{ "TreeView", "Tree View" },
			{ "ColumnView", "List View" },
			{ "GridView", "Data Grid" },
			{ "ColorButton", "Expander Input" },
			{ "FontButton", "Expander Input" },

			// Media.
			{ "Image", "Image" },
			{ "Picture", "Image" },

			// Menus.
			{ "Menu", "Menu Bar" },
			{ "MenuBar", "Menu Bar" },
			{ "MenuItem", "Menu Item" },
			{ "SeparatorMenuItem", "Menu Item" },
			{ "Statusbar", "Status Bar" },
			{ "Separator", "Border" },

			// Windows.
			{ "Window", "Window" },
			{ "Dialog", "Dialog" },
			{ "MessageDialog", "Dialog" },
			{ "AboutDialog", "Dialog" },

			// Libadwaita widgets, whose GIR names carry the same namespace prefix ("AdwHeaderBar").
			// They are GTK's own controls wearing a different library's name, so they resolve through
			// the same shared concepts.
			{ "HeaderBar", "Container" },
			{ "StatusPage", "Text" },
			{ "Clamp", "Container" },
			{ "PreferencesPage", "Tab" },
			{ "PreferencesGroup", "Expander" },
			{ "PreferencesDialog", "Dialog" },
			{ "ActionRow", "Button" },
			{ "EntryRow", "Text Input" },
			{ "SwitchRow", "Toggle Button" },
			{ "ButtonRow", "Button" },
			{ "ButtonContent", "Button" },
			{ "ComboRow", "Combo Box" },
			{ "SpinRow", "Numeric" },
			{ "ExpanderRow", "Expander" },
			{ "Avatar", "Image" },
			{ "Banner", "Label" },
			{ "Spinner", "Progress" },
			{ "ToolbarView", "Container" },
			{ "StatusBar", "Status Bar" },
			{ "OverlaySplitButton", "Split Button" },
			{ "ViewStack", "Tab" },
			{ "ViewSwitcher", "Tab" },
			{ "ViewSwitcherBar", "Tab" },
			{ "WindowTitle", "Text" },
			{ "AboutDialog", "Dialog" },
			{ "MessageDialog", "Dialog" },
		};

		public string? GetConcept(string typeName)
		{
			return typeName != null && concepts.TryGetValue(typeName, out var concept) ? concept : null;
		}

		public string? GetCategory(string typeName)
		{
			// GTK has no category concept of its own, so the shared one applies.
			return null;
		}
	}
}
