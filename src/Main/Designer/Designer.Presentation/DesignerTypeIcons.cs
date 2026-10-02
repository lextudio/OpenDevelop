// Per-type glyph resolution shared by every design surface that shows a type name: the Toolbox
// rows, the Document Outline and the component tray. Visual Studio gives each element its
// control-type glyph from the VS Image Library; those glyphs live in ICSharpCode.Core.Presentation
// under Resources/VS2026/<Name>/ and are themed by PresentationResourceService like every other
// VS2026 icon.
//
// This lives in Designer.Presentation rather than next to the outline control because the type
// name -> glyph mapping is runtime-neutral: the outline happens to consume it through a per-row
// Func<DesignerElementNode, ImageSource>, while a Toolbox row template wants the same answer for a
// DesignerToolboxItemInfo. Keeping one table is what stops the two pads from drifting apart.
//
// Every producer spells the type differently, so the name is normalized to a bare control name
// first. The previous copy lived beside the outline control as DocumentOutlineIcons.
//
// A framework whose own icons are available (WinForms reads them out of the loaded
// LibreWinForms assembly) passes its own resolver in ahead of this; a miss here is not an error,
// it means "no glyph of its own", and the caller draws its own placeholder.

using System;
using System.Collections.Generic;
using System.Windows.Media;

using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>Resolves the VS Image Library glyph for a design element's or toolbox item's type.</summary>
	public static class DesignerTypeIcons
	{
		/// <summary>The glyph used when a type has none of its own.</summary>
		public const string FallbackIconName = "Control";

		/// <summary>The glyph used for a non-visual (component tray) object without one of its own.</summary>
		public const string ComponentFallbackIconName = "Component";

		/// <summary>Maps a normalized control name onto the VS glyph it is drawn with. A designer
		/// with real icons of its own bypasses this; the entries here are for the types that have
		/// no framework icon to read, plus the framework types whose VS spelling differs.</summary>
		public static IReadOnlyDictionary<string, string> Aliases => aliases;

		static readonly Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal) {
			// Framework controls whose VS glyph is named after the layout or the concept rather than
			// after the WPF type.
			{ "Grid", "Table" },
			{ "StackPanel", "StackPanel" },
			{ "DockPanel", "DockPanel" },
			{ "WrapPanel", "StackPanel" },
			{ "Canvas", "Table" },
			{ "UniformGrid", "Grid" },
			{ "Label", "Label" },
			{ "TextBlock", "TextBox" },
			{ "TextBox", "TextBox" },
			{ "PasswordBox", "TextBox" },
			{ "RichTextBox", "TextBox" },
			{ "Button", "Button" },
			{ "RadioButton", "RadioButton" },
			{ "CheckBox", "CheckBoxChecked" },
			{ "ComboBox", "ComboBox" },
			{ "ListBox", "ListBox" },
			{ "ListView", "ListView" },
			{ "TreeView", "TreeView" },
			{ "TabControl", "Tab" },
			{ "Expander", "Expander" },
			{ "GroupBox", "GroupBox" },
			{ "Slider", "Slider" },
			{ "ProgressBar", "ProgressBar" },
			{ "Image", "Image" },
			{ "ScrollViewer", "ScrollViewer" },
			{ "Menu", "MenuBar" },
			{ "ContextMenu", "ContextMenu" },
			{ "ToolBar", "ToolBar" },
			{ "Separator", "Control" },
			{ "Calendar", "Calendar" },
			{ "DatePicker", "Calendar" },
			{ "Tooltip", "Tooltip" },
			{ "Window", "WindowScreenshot" },

			// WinForms types with no WPF counterpart by the same name.
			{ "CheckedListBox", "ListBox" },
			{ "CheckedListView", "ListView" },
			{ "ListViewGroup", "ListView" },
			{ "ListViewItem", "ListView" },
			{ "TreeNode", "TreeView" },
			{ "LinkLabel", "HyperLink" },
				{ "ToolStripMenuItem", "MenuItem" },
			{ "ToolStripItem", "ToolstripContainer" },
			{ "ToolStripButton", "Button" },
			{ "ToolStripDropDownButton", "ComboBox" },
			{ "ToolStripSplitButton", "ToolstripContainer" },
			{ "ToolStripSeparator", "Control" },
			{ "ToolstripContainer", "ToolstripContainer" },
			{ "ToolStripPanel", "ToolstripContainer" },
			{ "ToolStripStatusLabel", "StatusStrip" },
			{ "ToolStripProgressBar", "ProgressBar" },
			{ "ToolStripComboBox", "ComboBox" },
			{ "ToolStripTextBox", "TextBox" },
			{ "ToolStripHost", "ToolstripContainer" },
			{ "StatusStrip", "StatusStrip" },
			{ "DataGridView", "DataGrid" },
			{ "DataGridViewColumn", "Column" },
			{ "DataGridViewRow", "Column" },
			{ "DataTable", "DataTable" },
			{ "DataView", "DataGrid" },
			{ "DataSet", "DataGrid" },
			{ "PropertyGrid", "PropertyGridEditorPart" },
			{ "FolderBrowserDialog", "Dialog" },
			{ "OpenFileDialog", "Dialog" },
			{ "SaveFileDialog", "Dialog" },
			{ "MessageBox", "Dialog" },
			{ "ColorDialog", "Dialog" },
			{ "FontDialog", "Dialog" },
			{ "PrintDialog", "Dialog" },
			{ "PrintPreviewDialog", "PrintPreviewDialog" },
			{ "Panel", "Panel" },
			{ "SplitContainer", "Control" },
			{ "TableLayoutPanel", "Table" },
			{ "FlowLayoutPanel", "StackPanel" },
			{ "TablePanel", "Table" },
			{ "PictureBox", "Image" },
			{ "NumericUpDown", "Numeric" },
			{ "DomainUpDown", "Numeric" },
			{ "MonthCalendar", "Calendar" },
			{ "Timer", "Timer" },
			{ "BackgroundWorker", "BackgroundWorker" },
			{ "Process", "Process" },
			{ "EventLog", "EventLog" },
			{ "PerformanceCounter", "Process" },
			{ "FileSystemWatcher", "FileSystemWatcher" },
			{ "SerialPort", "SerialPort" },
			{ "PrintDocument", "PrintDocument" },
			{ "NotifyIcon", "Control" },
			{ "WebBrowser", "WebBrowser" },

			// GTK, WinUI and MewUI types mapped onto the WPF control they correspond to, so a GTK
			// "GtkButton" and a WinUI "Microsoft.UI.Xaml.Controls.Button" get the same glyph.
			{ "ToggleButton", "CheckBoxChecked" },
			{ "Switch", "ToggleButton" },
			{ "ProgressRing", "ProgressBar" },
			{ "InfoBar", "Label" },
			{ "SplitView", "Control" },
			{ "ItemsRepeater", "ListView" },
			{ "AutoSuggestBox", "ComboBox" },
			{ "ColorPicker", "ColorPicker" },
			{ "NumberBox", "Numeric" },
			{ "CalendarDatePicker", "Calendar" },
			{ "RepeatButton", "Button" },
			{ "ToggleSwitch", "ToggleButton" },
			{ "ScrollBar", "ScrollViewer" },
			{ "SliderTrack", "Slider" },
			{ "SelectionBar", "ListView" },
		};

		// Families of non-visual types too large to list, matched by name suffix after the exact and
		// alias lookups miss: SolidColorBrush, DoubleAnimationUsingKeyFrames, RotateTransform,
		// DropShadowEffect, DataTrigger, ObjectDataProvider, ...
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
		// there, so cache the misses here or every surface refresh would retry and re-log them.
		static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
		static readonly HashSet<string> misses = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>How many distinct icon names have fallen through every lookup and resolved to a
		/// fallback. Surfaced so it is visible which framework is under-delivering icons rather than
		/// being rediscovered by looking at a Toolbox with blank rows.</summary>
		public static int FallbackCount => misses.Count;

		/// <summary>The names that had no glyph of their own, for a diagnostic.</summary>
		public static IReadOnlyCollection<string> FallbackNames => misses;

		/// <summary>The bare control name of a type: namespace, XAML prefix, generic arity, nested
		/// type separator and the GTK "Gtk" prefix removed. "System.Windows.Forms.Button",
		/// "local:Button" and "GtkButton" all become "Button".</summary>
		public static string GetElementTypeName(string type)
		{
			if (string.IsNullOrWhiteSpace(type))
				return "";
			var name = type.Trim();
			var generic = name.IndexOf('`');
			if (generic >= 0)
				name = name.Substring(0, generic);
			name = name.Substring(name.LastIndexOf(':') + 1);
			name = name.Substring(name.LastIndexOf('.') + 1);
			name = name.Substring(name.LastIndexOf('+') + 1);
			// GTK spells its widget classes with the namespace glued on ("GtkButton"), and
			// Libadwaita does the same ("AdwHeaderBar"). Those are namespaces, not part of the
			// control's name, so a bare name is what a row shows and what a framework mapper keys
			// on. Anything whose fourth character is not an uppercase letter is left alone, so a
			// genuine "Gtk-something" type is not mangled.
			foreach (var prefix in new[] { "Gtk", "Adw" }) {
				if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)
					&& char.IsUpper(name[prefix.Length]))
					return name.Substring(prefix.Length);
			}
			return name;
		}

		/// <summary>The VS glyph for <paramref name="type"/>, or the <paramref name="fallbackIconName"/>
		/// glyph when that type has none (null for no fallback).</summary>
		public static ImageSource GetIcon(string type, string fallbackIconName = FallbackIconName, IDesignerControlMapper mapper = null)
		{
			var name = GetElementTypeName(type);
			ImageSource image = null;
			if (name.Length > 0) {
				// A framework mapper is asked first, because it is the only layer that knows the
				// framework's own vocabulary: "Entry" is GTK's TextBox and nothing else in the tree
				// can know that. Without one, the name table below handles the spellings that need
				// no framework knowledge (WinUI, WPF, the VS2017-era WinForms names).
				var concept = DesignerToolboxCatalog.ResolveControl(mapper, name);
				if (concept != null)
					image = GetGlyph(DesignerToolboxCatalog.GetIconName(concept) ?? concept);
				if (image == null)
					image = GetGlyph(aliases.TryGetValue(name, out var alias) ? alias : name);
				for (var i = 0; image == null && i < suffixes.Length; i++) {
					if (name.EndsWith(suffixes[i].Suffix, StringComparison.Ordinal))
						image = GetGlyph(suffixes[i].IconName);
				}
			}
			if (image == null && name.Length > 0)
				misses.Add(name);
			return image ?? (fallbackIconName == null ? null : GetGlyph(fallbackIconName));
		}

		/// <summary>The VS glyph for an outline node: its element type's glyph, falling back to the
		/// generic component glyph for tray objects and the generic control glyph otherwise.</summary>
		public static ImageSource GetIcon(DesignerElementNode node, IDesignerControlMapper mapper = null)
		{
			if (node == null)
				return null;
			return GetIcon(node.Type, node.IsTrayComponent ? ComponentFallbackIconName : FallbackIconName, mapper);
		}

		/// <summary>The VS glyph for a Toolbox item, from its type name. A designer whose framework
		/// ships real per-control icons reads those instead and only falls back here.</summary>
		public static ImageSource GetIcon(DesignerToolboxItemInfo item, IDesignerControlMapper mapper = null)
		{
			return item == null ? null : GetIcon(item.TypeName, FallbackIconName, mapper);
		}


	/// <summary>The content for one toolbox row: the item's glyph beside its display name. A
	/// designer whose framework supplies real per-control icons resolves those first and falls back
	/// to the VS Image Library glyph for the type; a type with neither gets the generic control glyph,
	/// so a row is never blank.</summary>
	public static object CreateToolboxRowContent(DesignerToolboxItemInfo item,
		Func<DesignerToolboxItemInfo, ImageSource> frameworkIcon = null,
		IDesignerControlMapper mapper = null)
	{
		var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
		var icon = frameworkIcon?.Invoke(item) ?? GetIcon(item, mapper);
		if (icon != null)
			row.Children.Add(new System.Windows.Controls.Image {
				Source = icon,
				Width = 16.0,
				Height = 16.0,
				Margin = new System.Windows.Thickness(0, 0, 4, 0),
				VerticalAlignment = System.Windows.VerticalAlignment.Center
			});
		row.Children.Add(new System.Windows.Controls.TextBlock {
			Text = string.IsNullOrEmpty(item?.DisplayName) ? item?.Name ?? "" : item.DisplayName,
			VerticalAlignment = System.Windows.VerticalAlignment.Center
		});
		return row;
	}

	/// <summary>An ItemsControl row template bound to <see cref="DesignerToolboxItemInfo"/>, for the
	/// designers whose Toolbox is a plain ListBox over the DTO rather than a SharedToolboxItem list.
	/// The icon is resolved per row through <paramref name="frameworkIcon"/> when given.</summary>
	public static System.Windows.DataTemplate CreateToolboxItemTemplate(
		Func<DesignerToolboxItemInfo, ImageSource> frameworkIcon = null,
		IDesignerControlMapper mapper = null)
	{
		var image = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Image));
		image.SetValue(System.Windows.Controls.Image.WidthProperty, 16.0);
		image.SetValue(System.Windows.Controls.Image.HeightProperty, 16.0);
		image.SetValue(System.Windows.FrameworkElement.MarginProperty, new System.Windows.Thickness(0, 0, 4, 0));
		image.SetValue(System.Windows.FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
		// No path: the binding's value is the row's own data item, which the converter turns into the
		// icon. (This was once Binding("Self") with the converter as its Source - a path to a property
		// the converter does not have - so every row's Image.Source was null and the rows showed an
		// empty gap where the glyph belonged.)
		image.SetBinding(System.Windows.Controls.Image.SourceProperty,
			new System.Windows.Data.Binding { Converter = new ToolboxIconConverter(frameworkIcon, mapper), Mode = System.Windows.Data.BindingMode.OneWay });

		var text = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
		text.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
			new System.Windows.Data.Binding(nameof(DesignerToolboxItemInfo.DisplayName)));
		text.SetValue(System.Windows.FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);

		var panel = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel));
		panel.SetValue(System.Windows.Controls.StackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
		panel.AppendChild(image);
		panel.AppendChild(text);

		return new System.Windows.DataTemplate { DataType = typeof(DesignerToolboxItemInfo), VisualTree = panel };
	}

	/// <summary>Feeds the row's Image.Source from the row's own data item, so a virtualizing or
	/// recycled ListBox row still shows the icon of the item it is currently showing.</summary>
	sealed class ToolboxIconConverter : System.Windows.Data.IValueConverter
	{
		readonly Func<DesignerToolboxItemInfo, ImageSource> frameworkIcon;
		readonly IDesignerControlMapper mapper;

		public ToolboxIconConverter(Func<DesignerToolboxItemInfo, ImageSource> frameworkIcon,
			IDesignerControlMapper mapper)
		{
			this.frameworkIcon = frameworkIcon;
			this.mapper = mapper;
		}

		public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
		{
			return value is DesignerToolboxItemInfo item ? frameworkIcon?.Invoke(item) ?? GetIcon(item, mapper) : null;
		}

		public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}

	static ImageSource GetGlyph(string iconName)
		{
			lock (cache) {
				if (!cache.TryGetValue(iconName, out var image)) {
					try {
						image = PresentationResourceService.GetImageSource("Icons.16x16." + iconName);
					} catch (ArgumentNullException) {
						// No resource service (e.g. a unit test host): the surface simply has no icons.
						return null;
					}
					cache[iconName] = image;
				}
				return image;
			}
		}
	}
}
