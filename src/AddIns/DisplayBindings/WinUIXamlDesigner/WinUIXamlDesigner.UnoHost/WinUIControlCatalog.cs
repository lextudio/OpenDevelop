using System.Collections.Generic;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// What the WinUI Toolbox shows for a control: its group and its default markup. Pure string logic with no framework
	/// dependency, so the hosts that reflect their catalog from a loaded runtime (<see cref="DesignHost"/>) and the Surface
	/// child, which has no WinUI runtime to reflect and advertises <see cref="StaticCatalog"/> instead, share one definition.
	/// </summary>
	internal static class WinUIControlCatalog
	{
		const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

		/// <summary>The common WinUI controls (Microsoft.UI.Xaml.Controls) offered when no runtime can be reflected.</summary>
		static readonly string[] CommonControls = {
			"Button", "RepeatButton", "ToggleButton", "HyperlinkButton", "RadioButton", "CheckBox", "ToggleSwitch",
			"TextBlock", "TextBox", "RichEditBox", "RichTextBlock", "PasswordBox", "AutoSuggestBox", "NumberBox",
			"Grid", "StackPanel", "RelativePanel", "Canvas", "VariableSizedWrapGrid", "Border", "ScrollViewer",
			"ListView", "GridView", "ListBox", "ComboBox", "TreeView", "Pivot",
			"Image", "SymbolIcon", "FontIcon", "BitmapIcon", "PathIcon",
			"NavigationView", "TabView", "SplitView", "CommandBar", "MenuBar", "PersonPicture",
			"ProgressBar", "ProgressRing", "RatingControl", "Slider",
			"InfoBar", "TeachingTip",
			"DatePicker", "TimePicker", "CalendarDatePicker", "CalendarView",
			"ColorPicker", "InkCanvas", "Expander"
		};

		/// <summary>The toolbox catalog as static data: <see cref="CommonControls"/> with their groups and default markup.</summary>
		internal static List<DesignerToolboxItemInfo> StaticCatalog()
		{
			var catalog = new List<DesignerToolboxItemInfo>();
			foreach (var name in CommonControls)
				catalog.Add(new DesignerToolboxItemInfo {
					Name = name,
					DisplayName = name,
					Category = Categorize(name),
					XamlNamespace = PresentationNamespace,
					Template = BuildDefaultTemplate(name)
				});
			return catalog;
		}

	/// <summary>Maps a WinUI control to its Toolbox group, mirroring the grouping the
	/// Fluent control gallery uses so the pad reads like the real WinUI toolbox.</summary>
	internal static string Categorize(string name)
	{
		switch (name)
		{
			case "Button":
			case "RepeatButton":
			case "ToggleButton":
			case "HyperlinkButton":
			case "AppBarButton":
			case "AppBarToggleButton":
			case "RadioButton":
			case "CheckBox":
			case "ToggleSwitch":
				return "Buttons";

			case "TextBlock":
			case "TextBox":
			case "RichEditBox":
			case "RichTextBlock":
			case "RichTextBlockOverflow":
			case "PasswordBox":
			case "AutoSuggestBox":
			case "NumberBox":
				return "Text";

			case "Grid":
			case "StackPanel":
			case "RelativePanel":
			case "Canvas":
			case "VariableSizedWrapGrid":
			case "Border":
			case "ItemsRepeater":
			case "ScrollViewer":
				return "Containers";

			case "ListView":
			case "GridView":
			case "ListBox":
			case "ComboBox":
			case "ItemsView":
			case "SelectorBar":
			case "TreeView":
			case "Pivot":
				return "Lists";

			case "Image":
			case "SymbolIcon":
			case "FontIcon":
			case "BitmapIcon":
			case "PathIcon":
			case "IconSourceElement":
				return "Media";

			case "NavigationView":
			case "TabView":
			case "SplitView":
			case "CommandBar":
			case "MenuBar":
			case "PersonPicture":
				return "Navigation";

			case "ProgressBar":
			case "ProgressRing":
			case "RatingControl":
			case "Slider":
				return "Status & Progress";

			case "InfoBar":
			case "TeachingTip":
			case "ToolTip":
				return "Feedback";

			case "DatePicker":
			case "TimePicker":
			case "CalendarDatePicker":
			case "CalendarView":
				return "Date & Time";

			case "ColorPicker":
			case "InkCanvas":
			case "InkToolbar":
				return "Ink & Color";

			default:
				return "Common";
		}
	}

	/// <summary>Matches the default content the host's editor gives inserted controls.
	/// Controls that need a size or a value to be useful on the canvas get sensible
	/// initial properties; everything else is inserted as an empty element.</summary>
	internal static string BuildDefaultTemplate(string name)
	{
		var ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
		switch (name)
		{
			case "TextBlock":
				return $"<TextBlock xmlns=\"{ns}\" Text=\"TextBlock\"/>";
			case "TextBox":
				return $"<TextBox xmlns=\"{ns}\" Text=\"TextBox\"/>";
			case "RichEditBox":
				return $"<RichEditBox xmlns=\"{ns}\" IsReadOnly=\"False\" Height=\"120\"/>";
			case "PasswordBox":
				return $"<PasswordBox xmlns=\"{ns}\" Password=\"PasswordBox\"/>";
			case "NumberBox":
				return $"<NumberBox xmlns=\"{ns}\" Value=\"0\"/>";
			case "Button":
			case "CheckBox":
			case "HyperlinkButton":
			case "RadioButton":
			case "ToggleSwitch":
				return $"<{name} xmlns=\"{ns}\" Content=\"{name}\"/>";
			case "RepeatButton":
			case "ToggleButton":
			case "AppBarButton":
			case "AppBarToggleButton":
				return $"<{name} xmlns=\"{ns}\" Content=\"{name}\"/>";
			case "Slider":
				return $"<Slider xmlns=\"{ns}\" Width=\"200\" Minimum=\"0\" Maximum=\"100\" Value=\"50\"/>";
			case "ProgressBar":
				return $"<ProgressBar xmlns=\"{ns}\" Width=\"200\" Value=\"40\"/>";
			case "ProgressRing":
				return $"<ProgressRing xmlns=\"{ns}\" Width=\"60\" Height=\"60\" IsActive=\"True\"/>";
			case "RatingControl":
				return $"<RatingControl xmlns=\"{ns}\" Value=\"3\"/>";
			case "Image":
				return $"<Image xmlns=\"{ns}\" Width=\"200\" Height=\"120\"/>";
			case "AutoSuggestBox":
				return $"<AutoSuggestBox xmlns=\"{ns}\" PlaceholderText=\"Search\"/>";
			case "InfoBar":
				return $"<InfoBar xmlns=\"{ns}\" Title=\"Title\" Message=\"Message\" IsOpen=\"True\"/>";
			case "CalendarDatePicker":
				return $"<CalendarDatePicker xmlns=\"{ns}\" Header=\"Date\"/>";
			case "ColorPicker":
				return $"<ColorPicker xmlns=\"{ns}\" Color=\"#FF0078D4\"/>";
			case "ComboBox":
				return $"<ComboBox xmlns=\"{ns}\" PlaceholderText=\"Select\"><ComboBoxItem Content=\"Item 1\"/><ComboBoxItem Content=\"Item 2\"/></ComboBox>";
			case "ListBox":
				return $"<ListBox xmlns=\"{ns}\" Width=\"180\" Height=\"120\"><ListBoxItem Content=\"Item 1\"/><ListBoxItem Content=\"Item 2\"/></ListBox>";
			case "ListView":
				return $"<ListView xmlns=\"{ns}\" Width=\"240\" Height=\"160\"><ListViewItem Content=\"Item 1\"/><ListViewItem Content=\"Item 2\"/></ListView>";
			case "GridView":
				return $"<GridView xmlns=\"{ns}\" Width=\"240\" Height=\"160\"><GridViewItem Content=\"Item 1\"/><GridViewItem Content=\"Item 2\"/></GridView>";
			default:
				return $"<{name} xmlns=\"{ns}\"/>";
		}
	}
	}
}
