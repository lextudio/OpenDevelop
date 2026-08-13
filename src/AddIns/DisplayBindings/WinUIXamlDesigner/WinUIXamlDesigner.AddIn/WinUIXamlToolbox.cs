using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>
/// Content for the shell's shared Toolbox pad while a WinUI/Uno document is active. ProGPU's own
/// <c>ProGPU.WinUI.Designer.Toolbox</c> is intentionally not used: it is a Microsoft.UI.Xaml
/// control that would render inside the ProGPU surface instead of the IDE's pad, which would
/// diverge from the WinForms and WPF designers.
/// </summary>
public sealed class WinUIXamlToolbox
{
	const string StandardControlsCategory = "WinUI / Uno";

	static WinUIXamlToolbox instance;

	public static WinUIXamlToolbox Instance {
		get {
			SD.MainThread.VerifyAccess();
			return instance ??= new WinUIXamlToolbox();
		}
	}

	readonly ListBox toolbox = new();
	readonly CollectionViewSource itemsView = new();
	readonly List<WinUIToolboxItem> items = new();

	WinUIXamlToolbox()
	{
		// First milestone stays inside the standard-control whitelist the technote calls for;
		// project custom controls arrive with the isolated-loading phase.
		foreach (var name in new[] {
			"Border", "Button", "CheckBox", "ComboBox", "Grid", "HyperlinkButton", "Image",
			"ItemsControl", "ListView", "ProgressBar", "ProgressRing", "RadioButton",
			"ScrollViewer", "Slider", "StackPanel", "TextBlock", "TextBox", "ToggleSwitch"
		}) {
			items.Add(new WinUIToolboxItem(name, StandardControlsCategory));
		}

		// Populate before handing the list to the view: List<T> raises no collection-change
		// notification, so a view created over the still-empty list would never pick the items up
		// and would report zero groups forever.
		itemsView.Source = items;
		itemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WinUIToolboxItem.CategoryName)));

		toolbox.ItemsSource = itemsView.View;
		toolbox.DisplayMemberPath = nameof(WinUIToolboxItem.Name);

		// Same reason WpfToolbox disables it: with virtualization on, ContainerFromItem can report
		// a recycled row whose on-screen position does not match where the item actually renders,
		// so a synthetic press at those coordinates lands on a different row.
		VirtualizingStackPanel.SetIsVirtualizing(toolbox, false);
		toolbox.PreviewMouseLeftButtonDown += OnToolboxMouseDown;
		toolbox.PreviewMouseMove += OnToolboxMouseMove;
	}

	/// <summary>Data format carrying a dragged tool from this pad to a WinUI/Uno design surface.</summary>
	public const string DragDataFormat = "OpenDevelop.WinUIToolboxItem";

	Point dragStartPoint;
	WinUIToolboxItem dragStartItem;
	// Guards against the re-entrant moves a portable drag delivers while DoDragDrop blocks -
	// same hazard WpfToolbox documents.
	bool isDragging;

	void OnToolboxMouseDown(object sender, MouseButtonEventArgs e)
	{
		dragStartPoint = e.GetPosition(toolbox);
		dragStartItem = (e.OriginalSource as DependencyObject).FindAncestorItem(toolbox);
	}

	void OnToolboxMouseMove(object sender, MouseEventArgs e)
	{
		if (isDragging || dragStartItem == null || e.LeftButton != MouseButtonState.Pressed)
			return;
		var position = e.GetPosition(toolbox);
		if (System.Math.Abs(position.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
			&& System.Math.Abs(position.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
			return;

		var item = dragStartItem;
		dragStartItem = null;
		isDragging = true;
		try {
			toolbox.SelectedItem = item;
			DragDrop.DoDragDrop(toolbox, new DataObject(DragDataFormat, item.Name), DragDropEffects.Copy);
		} finally {
			isDragging = false;
		}
	}

	public object ToolboxControl => toolbox;

	/// <summary>The tool the user has selected, or null when the pad has no selection.</summary>
	public WinUIToolboxItem SelectedItem => toolbox.SelectedItem as WinUIToolboxItem;

	public int ItemCount => items.Count;

	public int GroupCount => itemsView.View?.Groups?.Count ?? 0;

	/// <summary>
	/// Looks a tool up by the name the pad actually displays, so an insertion driven through this
	/// cannot succeed for a control the Toolbox does not offer.
	/// </summary>
	public WinUIToolboxItem FindItem(string name) =>
		items.FirstOrDefault(item => string.Equals(item.Name, name, System.StringComparison.OrdinalIgnoreCase));
}

static class ToolboxVisualExtensions
{
	/// <summary>Walks up from the pressed visual to the ListBoxItem's bound tool.</summary>
	public static WinUIToolboxItem FindAncestorItem(this DependencyObject source, ListBox owner)
	{
		while (source != null && source != owner) {
			if (source is ListBoxItem row)
				return row.DataContext as WinUIToolboxItem;
			source = System.Windows.Media.VisualTreeHelper.GetParent(source)
				?? System.Windows.LogicalTreeHelper.GetParent(source);
		}
		return null;
	}
}

public sealed class WinUIToolboxItem
{
	public WinUIToolboxItem(string name, string categoryName)
	{
		Name = name;
		CategoryName = categoryName;
	}

	public string Name { get; }
	public string CategoryName { get; }
}
