// Per-row icons for the shared Document Outline (DocumentOutlineControl), matching Visual
// Studio's Document Outline, which gives every element its control-type glyph from the VS Image
// Library. The glyphs live in ICSharpCode.Core.Presentation under Resources/VS2026/<Name>/ and are
// themed by PresentationResourceService like every other VS2026 icon.
//
// Every outline producer spells DesignerElementNode.Type differently, so the type is normalized to
// a bare control name before the lookup:
//   * WPF surface host, Uno host, MewUI:  short CLR name    "Button"
//   * WinForms host, WPF preview host:     full CLR name     "System.Windows.Forms.Button"
//   * WinUI source fallback:               XAML local name   "Button" (a prefix is dropped too)
//   * GTK host:                            GTK class name    "GtkButton"
//   * code-editor outlines:                symbol kind       "Class", "Method", ...
// WinForms designers pass their own toolbox icons instead (DocumentOutlineControl.IconSelector),
// so those rows look like the WinForms Toolbox.

using System;
using System.Collections.Generic;
using System.Windows.Media;

using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Widgets
{
	/// <summary>Resolves the VS Image Library glyph for an outline node's element type.</summary>
	public static class DocumentOutlineIcons
	{
		/// <summary>The glyph used when a designer element's type has none of its own.</summary>
		public const string FallbackIconName = "Control";

		/// <summary>The glyph used for a non-visual (component tray) object without one of its own.</summary>
		public const string ComponentFallbackIconName = "Component";

		// Element types whose VS glyph is named differently. WinUI and GTK types map onto the WPF
		// control they correspond to; code-outline symbol kinds map onto the VS2026 symbol glyphs.
		static readonly Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal) {
			// Visual Studio's own Document Outline uses these two.
			{ "Window", "Application" },
			{ "Button", "ButtonClick" },
			{ "Page", "Application" },
			{ "RepeatButton", "ButtonClick" },
			{ "AppBarButton", "ButtonClick" },
			{ "HyperlinkButton", "HyperLink" },
			{ "Hyperlink", "HyperLink" },
			{ "DropDownButton", "SplitButton" },
			{ "AppBarToggleButton", "ToggleButton" },
			{ "ToggleSwitch", "ToggleButton" },
			{ "CheckBox", "CheckBoxChecked" },
			{ "Canvas", "CanvasElement" },
			{ "Border", "BorderElement" },
			{ "ContentControl", "ContentControlElement" },
			{ "Frame", "ContentControlElement" },
			{ "TabControl", "Tab" },
			{ "TabItem", "Tab" },
			{ "TabView", "Tab" },
			{ "TabViewItem", "Tab" },
			{ "Menu", "MenuBar" },
			{ "MenuBarItem", "MenuItem" },
			{ "MenuFlyout", "ContextMenu" },
			{ "MenuFlyoutItem", "MenuItem" },
			{ "MenuFlyoutSubItem", "MenuItem" },
			{ "Separator", "MenuSeparator" },
			{ "MenuFlyoutSeparator", "MenuSeparator" },
			{ "Popup", "PopupControl" },
			{ "Flyout", "PopupControl" },
			{ "DatePicker", "DateTimePicker" },
			{ "CalendarDatePicker", "DateTimePicker" },
			{ "CalendarView", "Calendar" },
			{ "ListBoxItem", "ListBox" },
			{ "ListViewItem", "ListView" },
			{ "GridView", "ListView" },
			{ "GridViewItem", "ListView" },
			{ "TreeViewItem", "TreeView" },
			{ "RichEditBox", "RichTextBox" },
			{ "RichTextBlock", "TextBlock" },
			{ "NumberBox", "TextBox" },
			{ "AutoSuggestBox", "ComboBox" },
			{ "ProgressRing", "ProgressBar" },
			{ "RatingControl", "Rating" },
			{ "UniformGrid", "Grid" },
			{ "RelativePanel", "Panel" },
			{ "VirtualizingStackPanel", "StackPanel" },
			{ "Polyline", "Polygon" },
			{ "StatusBar", "ToolBar" },
			// Non-visual objects (resources, data, timers, ...) that the designers show in the
			// component tray.
			{ "Style", "StyleBlock" },
			{ "ResourceDictionary", "ResourceView" },
			{ "CollectionViewSource", "DataSource" },
			{ "Storyboard", "Animation" },
			{ "MediaElement", "Media" },
			{ "MediaPlayerElement", "Media" },
			{ "DispatcherTimer", "Timer" },
			{ "FontFamily", "Font" },
			// GTK (after the "Gtk" prefix is dropped).
			{ "Entry", "TextBox" },
			{ "TextView", "TextBox" },
			{ "SpinButton", "TextBox" },
			{ "CheckButton", "CheckBoxChecked" },
			{ "Switch", "ToggleButton" },
			{ "ComboBoxText", "ComboBox" },
			{ "Box", "StackPanel" },
			{ "ScrolledWindow", "ScrollViewer" },
			{ "Notebook", "Tab" },
			{ "Scale", "Slider" },
			{ "Paned", "GridSplitter" },
			{ "HeaderBar", "ToolBar" },
			{ "Toolbar", "ToolBar" },
			// Code-outline symbol kinds.
			{ "Enum", "Enumeration" },
			{ "Struct", "Structure" },
			{ "Variable", "LocalVariable" },
			{ "Constructor", "Method" },
			{ "Function", "Method" },
			{ "Object", "Control" },
		};

		// Families of non-visual types too large to list, matched by name suffix after the exact
		// and alias lookups miss: SolidColorBrush, DoubleAnimationUsingKeyFrames, RotateTransform,
		// DropShadowEffect, DataTrigger, ObjectDataProvider, PrintPreviewDialog, ...
		static readonly (string Suffix, string IconName)[] suffixes = {
			("Brush", "Brush"),
			("AnimationUsingKeyFrames", "Animation"),
			("Animation", "Animation"),
			("Transform", "Transform"),
			("Effect", "Effect"),
			("Trigger", "Trigger"),
			("Behavior", "Behavior"),
			("Dialog", "Dialog"),
			("Template", "Template"),
			("DataProvider", "DataSource"),
			("Timer", "Timer"),
		};

		// A lookup that misses logs a warning inside PresentationResourceService and is not cached
		// there, so cache the misses here or every outline refresh would retry and re-log them.
		static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.Ordinal);

		/// <summary>The bare control name of an outline node's type: namespace, XAML prefix and
		/// GTK "Gtk" prefix removed. "System.Windows.Forms.Button", "local:Button" and "GtkButton"
		/// all become "Button".</summary>
		public static string GetElementTypeName(string type)
		{
			if (String.IsNullOrWhiteSpace(type))
				return "";
			var name = type.Trim();
			var generic = name.IndexOf('`');
			if (generic >= 0)
				name = name.Substring(0, generic);
			name = name.Substring(name.LastIndexOf(':') + 1);
			name = name.Substring(name.LastIndexOf('.') + 1);
			name = name.Substring(name.LastIndexOf('+') + 1);
			if (name.Length > 3 && name.StartsWith("Gtk", StringComparison.Ordinal) && Char.IsUpper(name[3]))
				name = name.Substring(3);
			return name;
		}

		/// <summary>The VS glyph for <paramref name="type"/>, or the <paramref name="fallbackIconName"/>
		/// glyph when that type has none (null for no fallback).</summary>
		public static ImageSource GetIcon(string type, string fallbackIconName = FallbackIconName)
		{
			var name = GetElementTypeName(type);
			ImageSource image = null;
			if (name.Length > 0) {
				image = GetGlyph(aliases.TryGetValue(name, out var alias) ? alias : name);
				for (var i = 0; image == null && i < suffixes.Length; i++) {
					if (name.EndsWith(suffixes[i].Suffix, StringComparison.Ordinal))
						image = GetGlyph(suffixes[i].IconName);
				}
			}
			return image ?? (fallbackIconName == null ? null : GetGlyph(fallbackIconName));
		}

		/// <summary>The VS glyph for an outline node: its element type's glyph, falling back to the
		/// generic component glyph for tray objects and the generic control glyph otherwise.</summary>
		public static ImageSource GetIcon(DesignerElementNode node)
		{
			if (node == null)
				return null;
			return GetIcon(node.Type, node.IsTrayComponent ? ComponentFallbackIconName : FallbackIconName);
		}

		static ImageSource GetGlyph(string iconName)
		{
			lock (cache) {
				if (!cache.TryGetValue(iconName, out var image)) {
					try {
						image = PresentationResourceService.GetImageSource("Icons.16x16." + iconName);
					} catch (ArgumentNullException) {
						// No resource service (e.g. a unit test host): the outline simply has no icons.
						return null;
					}
					cache[iconName] = image;
				}
				return image;
			}
		}
	}
}
