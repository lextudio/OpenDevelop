using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>
/// The WinUI/Uno facade over the merged <see cref="SharedToolbox"/> engine (see that class's own
/// header comment for why WpfToolbox and this class no longer each own an independent copy of
/// the same ListBox/drag/selection state machine). ProGPU's own
/// <c>ProGPU.WinUI.Designer.Toolbox</c> is intentionally not used: it is a Microsoft.UI.Xaml
/// control that would render inside the ProGPU surface instead of the IDE's pad, which would
/// diverge from the WinForms and WPF designers.
/// </summary>
public sealed class WinUIXamlToolbox
{
	const string StandardControlsCategory = "WinUI / Uno";
	const string Scope = "winui";

	static WinUIXamlToolbox instance;

	public static WinUIXamlToolbox Instance {
		get {
			SD.MainThread.VerifyAccess();
			return instance ??= new WinUIXamlToolbox();
		}
	}

	WinUIXamlToolbox()
	{
	}

	/// <summary>
	/// Replaces the tool list with the active document's runtime catalog (the controls the
	/// project's actual Uno version loads), falling back to the standard whitelist when the
	/// host has not reported one yet.
	/// </summary>
	public void PopulateFromCatalog(IReadOnlyList<DesignerToolboxItemInfo> catalog)
	{
		if (catalog == null || catalog.Count == 0)
			return;
		var newItems = new List<SharedToolboxItem>();
		foreach (var tool in catalog)
		{
			if (string.IsNullOrWhiteSpace(tool.Name))
				continue;
			var category = string.IsNullOrEmpty(tool.Category) ? StandardControlsCategory : tool.Category;
			newItems.Add(new SharedToolboxItem(category, tool.Name, Scope,
				payload: new WinUIToolboxItem(tool.Name, category, tool.Template),
				packDragData: data => {
					data.SetData(DragDataFormat, tool.Name);
					// Also carries "ComponentTypeName" (the same format WpfToolbox uses) so
					// dropping a WinUI/Uno tool onto the plain XAML source editor -
					// AvalonEditViewContent.TextArea_Drop, which only ever looks for that one
					// format - inserts "<Tag />" there too, not just onto the ProGPU design
					// surface (WinUIXamlHost.OnDrop, which reads DragDataFormat instead).
					data.SetData("ComponentTypeName", tool.Name);
				}));
		}
		SharedToolbox.Instance.AddItems(newItems);
	}

	/// <summary>Data format carrying a dragged tool from this pad to a WinUI/Uno design surface.</summary>
	public const string DragDataFormat = "OpenDevelop.WinUIToolboxItem";

	public object ToolboxControl {
		get {
			SharedToolbox.Instance.SetActiveScopes(Scope);
			return SharedToolbox.Instance.ToolboxControl;
		}
	}

	/// <summary>The tool the user has selected, or null when the pad has no selection.</summary>
	public WinUIToolboxItem SelectedItem => SharedToolbox.Instance.SelectedItem?.Payload as WinUIToolboxItem;

	public int ItemCount {
		get {
			SharedToolbox.Instance.SetActiveScopes(Scope);
			return SharedToolbox.Instance.ItemCount(Scope);
		}
	}

	public int GroupCount {
		get {
			SharedToolbox.Instance.SetActiveScopes(Scope);
			return SharedToolbox.Instance.GroupCount;
		}
	}

	/// <summary>
	/// Looks a tool up by the name the pad actually displays, so an insertion driven through this
	/// cannot succeed for a control the Toolbox does not offer.
	/// </summary>
	public WinUIToolboxItem FindItem(string name) =>
		SharedToolbox.Instance.FindItem(Scope, name)?.Payload as WinUIToolboxItem;

	/// <summary>The actual row bound into the shared ListBox's ItemsSource for a tool name - use
	/// this (not <see cref="FindItem"/>'s <see cref="WinUIToolboxItem"/> payload) for anything
	/// that needs to select/scroll/realize the row itself, e.g.
	/// <c>ListBox.SelectedItem</c>/<c>ItemContainerGenerator.ContainerFromItem</c> in DevFlow's
	/// toolbox-bounds actions.</summary>
	public object FindListBoxItem(string name) => SharedToolbox.Instance.FindItem(Scope, name);
}

/// <summary>Thin, host-agnostic-shape-preserving wrapper kept only because callers outside this
/// file (DevFlow actions, WinUIXamlDesignerViewContent, WinUIXamlHost) already read
/// <c>.Name</c>/<c>.Template</c> off whatever <see cref="WinUIXamlToolbox.FindItem"/> returns -
/// the actual ListBox/drag/selection engine lives in <see cref="SharedToolbox"/> now.</summary>
public sealed class WinUIToolboxItem
{
	public WinUIToolboxItem(string name, string categoryName, string template = "")
	{
		Name = name;
		CategoryName = categoryName;
		Template = template;
	}

	public string Name { get; }
	public string CategoryName { get; }

	/// <summary>The runtime's default XAML for this control (used for drop previews).</summary>
	public string Template { get; }
}
