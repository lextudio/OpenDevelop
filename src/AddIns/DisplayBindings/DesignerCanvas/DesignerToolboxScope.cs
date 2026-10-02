// A designer's Toolbox, as rows of the one shared Tools pad list (SharedToolbox) - the same list the
// WPF, WinForms and WinUI designers use. The GTK and MewUI designers each used to build a private
// ListBox with its own copy of the drag-start state machine, row hit-testing and selection
// syncing; they now describe their controls as DesignerToolboxItemInfo and this class does the rest.
//
// SharedToolbox is a single list shared by every open document. Rows are grouped by scope ("gtk",
// "gtk-adw", "mewui"), and a designer shows its scopes when it hands the list to the Tools pad.
// Several documents of one framework share those rows, so an invoked or selected row is acted on
// only by the designer the list was last activated for (SharedToolbox.ActiveOwner).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.SharpDevelop.Designer.Surface;

public sealed class DesignerToolboxScope
{
	readonly object owner;
	readonly IDesignerControlMapper? mapper;
	readonly HashSet<string> registeredScopes = new(StringComparer.Ordinal);
	string[] activeScopes = Array.Empty<string>();

	/// <param name="owner">The designer this toolbox belongs to.</param>
	/// <param name="mapper">The framework's control mapper, for the rows' icons.</param>
	public DesignerToolboxScope(object owner, IDesignerControlMapper? mapper)
	{
		this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
		this.mapper = mapper;
		Toolbox.ItemInvoked += (_, item) => {
			if (IsActive && item?.Payload is DesignerToolboxItemInfo info) ItemInvoked?.Invoke(this, info);
		};
	}

	static SharedToolbox Toolbox => SharedToolbox.Instance;

	/// <summary>Raised when the user double-clicks, or presses Enter on, one of this designer's rows.</summary>
	public event EventHandler<DesignerToolboxItemInfo>? ItemInvoked;

	/// <summary>Adds a scope's rows to the shared list (once per scope; the rows are shared by every
	/// document of the framework).</summary>
	public void Register(string scope, IEnumerable<DesignerToolboxItemInfo> items)
	{
		if (!registeredScopes.Add(scope)) return;
		Toolbox.AddItems(items.Select(info => new SharedToolboxItem(
			string.IsNullOrEmpty(info.Category) ? DesignerToolboxCatalog.OtherCategory : info.Category,
			string.IsNullOrEmpty(info.DisplayName) ? info.Name : info.DisplayName,
			scope,
			icon: DesignerTypeIcons.GetIcon(info, mapper),
			payload: info,
			packDragData: data => ToolboxDragData.Pack(data, info.TypeName))));
	}

	/// <summary>The scopes this document shows - e.g. the Libadwaita rows only for a document that
	/// requires libadwaita. Applied now if this designer's list is the visible one.</summary>
	public void SetScopes(params string[] scopes)
	{
		activeScopes = scopes;
		if (IsActive) Activate();
	}

	/// <summary>Shows this designer's scopes and returns the control for <see cref="IToolsHost.ToolsContent"/>.</summary>
	public object Activate()
	{
		Toolbox.SetActiveScopes(owner, false, activeScopes);
		return Toolbox.ToolboxControl;
	}

	/// <summary>Whether the shared list currently shows this designer's rows.</summary>
	public bool IsActive => ReferenceEquals(Toolbox.ActiveOwner, owner);

	public ListBox Control => (ListBox)Toolbox.ToolboxControl;

	/// <summary>Whether the real Tools pad is showing the shared list on this designer's behalf.</summary>
	public bool IsHosted => IsActive && ReferenceEquals((SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HostedContent, Toolbox.ToolboxControl);

	/// <summary>The row the user selected, when it is one of this designer's.</summary>
	public DesignerToolboxItemInfo? SelectedItem => IsActive ? Toolbox.SelectedItem?.Payload as DesignerToolboxItemInfo : null;

	public bool Select(string typeName)
	{
		Activate();
		var row = Rows().FirstOrDefault(item => (item.Payload as DesignerToolboxItemInfo)?.TypeName == typeName);
		if (row == null) return false;
		Toolbox.Select(row);
		return true;
	}

	public void Filter(string text) { Activate(); Toolbox.Filter(text); }
	public string FilterText => Toolbox.FilterText;

	/// <summary>The rows the list shows for this document right now (after any filter).</summary>
	public int VisibleItemCount { get { Activate(); return Toolbox.VisibleItemCount; } }

	/// <summary>Whether <paramref name="typeName"/> is a control this document's toolbox offers.</summary>
	public bool Offers(string? typeName) => typeName != null && Rows().Any(item => (item.Payload as DesignerToolboxItemInfo)?.TypeName == typeName);

	/// <summary>
	/// Selects <paramref name="typeName"/>'s row, scrolls it into view, and waits until a hit test at its
	/// centre actually resolves back to it - so the bounds a test reads are ones a real synthetic press
	/// lands on. Null, with <paramref name="error"/> saying why, when there is no such row or it never
	/// settles.
	///
	/// ScrollIntoView/BringIntoView update layout synchronously, so PointToScreen reports the post-scroll
	/// position at once, but the pointer hits the last RENDERED frame, which lags by at least one
	/// compose. Measured on the GTK designer: the GtkSwitch row's coordinates pressed the GtkCheckButton
	/// row beside it, and the drag then carried the wrong control. InputHitTest, not
	/// VisualTreeHelper.HitTest, which on this stack routes through the compositor scene and reports
	/// stale results for layout-only elements.
	/// </summary>
	public FrameworkElement? SettledRow(string typeName, out string error, int timeoutMilliseconds = 4000)
	{
		if (!Select(typeName)) { error = "Toolbox has no row for: " + typeName; return null; }
		var list = Control;
		var row = Toolbox.SelectedItem;
		list.ScrollIntoView(row);
		list.UpdateLayout();
		if (FindContainer(list, row) is not FrameworkElement container) { error = "Toolbox row has no realized container (not scrolled into view?): " + typeName; return null; }
		container.BringIntoView();
		list.UpdateLayout();
		for (var elapsed = 0; ; elapsed += 100) {
			var centre = container.TranslatePoint(new Point(container.RenderSize.Width / 2, container.RenderSize.Height / 2), list);
			if (list.InputHitTest(centre) is DependencyObject hit && IsWithin(hit, container)) { error = ""; return container; }
			if (elapsed >= timeoutMilliseconds) { error = "Toolbox row never settled at its own layout position (scroll/render lag): " + typeName; return null; }
			PumpFor(100);
			list.UpdateLayout();
		}
	}

	static ListBoxItem? FindContainer(DependencyObject node, object? item)
	{
		for (int i = 0, count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i < count; i++) {
			var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
			if (child is ListBoxItem row && ReferenceEquals(row.DataContext, item)) return row;
			if (FindContainer(child, item) is { } found) return found;
		}
		return null;
	}

	static bool IsWithin(DependencyObject hit, DependencyObject container)
	{
		for (var current = hit; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
			if (ReferenceEquals(current, container)) return true;
		return false;
	}

	static void PumpFor(int milliseconds)
	{
		var frame = new System.Windows.Threading.DispatcherFrame();
		var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds),
			System.Windows.Threading.DispatcherPriority.Background, (_, _) => frame.Continue = false, System.Windows.Threading.Dispatcher.CurrentDispatcher);
		timer.Start();
		try { System.Windows.Threading.Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
	}

	IEnumerable<SharedToolboxItem> Rows() => activeScopes.SelectMany(scope => Toolbox.Items(scope));
}
