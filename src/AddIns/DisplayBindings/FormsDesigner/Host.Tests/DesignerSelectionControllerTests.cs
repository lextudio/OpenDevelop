using System.ComponentModel;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

public sealed class DesignerSelectionControllerTests
{
	[Fact]
	public void PadControllerSynchronizesTreeSelectionAndPropertiesWithoutOutlineReentry()
	{
		var selection = new DesignerSelectionController(node => "property:" + node.Id);
		IReadOnlyList<DesignerElementNode> roots = Array.Empty<DesignerElementNode>();
		object? property = null;
		string? outlineSelection = null;
		using var pads = new DesignerPadController(selection, value => roots = value, value => property = value, value => outlineSelection = value);

		pads.UpdateTree(Tree("root", Tree("button")));
		Assert.Single(roots);
		Assert.True(pads.CommitOutlineSelection("button"));
		Assert.Equal("property:button", property);
		Assert.Equal("button", outlineSelection);
	}

	[Fact]
	public void CommonDevFlowResultsKeepStableToolboxContract()
	{
		using var json = System.Text.Json.JsonDocument.Parse(DesignerDevFlowResults.ToolboxFilter(true, "button", 2, "Button"));
		var root = json.RootElement;
		Assert.True(root.GetProperty("success").GetBoolean());
		Assert.Equal("button", root.GetProperty("filterText").GetString());
		Assert.Equal(2, root.GetProperty("itemCount").GetInt32());
		Assert.Equal("Button", root.GetProperty("selectedItem").GetString());
	}

	[Fact]
	public void CommonDevFlowResultsKeepStableMultiSelectionContract()
	{
		using var json = System.Text.Json.JsonDocument.Parse(DesignerDevFlowResults.Selection(true, new[] { "primary", "secondary" }));
		var root = json.RootElement;
		Assert.True(root.GetProperty("success").GetBoolean());
		Assert.Equal("primary", root.GetProperty("primarySelectedId").GetString());
		Assert.Equal(2, root.GetProperty("selectionCount").GetInt32());
		Assert.Equal(new[] { "primary", "secondary" }, root.GetProperty("selectedIds").EnumerateArray().Select(item => item.GetString()).ToArray());
	}

	[Fact]
	public void PadControllerPreservesEmptyRootId()
	{
		var selection = new DesignerSelectionController(node => node);
		string? outlineSelection = null;
		using var pads = new DesignerPadController(selection, _ => { }, _ => { }, value => outlineSelection = value);

		pads.UpdateTree(Tree(""));
		selection.Select("");

		Assert.Equal("", outlineSelection);
	}

	[Fact]
	public void MultiSelectionKeepsPrimaryOrderAndSupportsSetOperations()
	{
		var controller = new DesignerSelectionController();
		controller.UpdateTree(Tree("root", Tree("one"), Tree("two"), Tree("three")));

		Assert.True(controller.Select(new[] { "two", "one" }));
		Assert.Equal(new[] { "two", "one" }, controller.SelectedIds);
		Assert.Equal("two", controller.PrimarySelectedId);
		Assert.True(controller.Select(new[] { "three" }, DesignerSelectionOperation.Add));
		Assert.Equal(new[] { "two", "one", "three" }, controller.SelectedIds);
		Assert.True(controller.Select(new[] { "one", "three" }, DesignerSelectionOperation.Toggle));
		Assert.Equal(new[] { "two" }, controller.SelectedIds);
	}

	[Fact]
	public void MultiSelectionRebuildRestoresEveryStableIdAndCreatesBatchPropertyContext()
	{
		var batches = 0;
		var controller = new DesignerSelectionController(node => node.Id,
			nodes => "batch:" + ++batches + ":" + string.Join(",", nodes.Select(node => node.Id)));
		controller.UpdateTree(Tree("root", Tree("one"), Tree("two")));
		controller.Select(new[] { "one", "two" });
		Assert.Equal("batch:1:one,two", controller.SelectedPropertyObject);

		controller.UpdateTree(Tree("root", Tree("two"), Tree("one")));

		Assert.Equal(new[] { "one", "two" }, controller.SelectedIds);
		Assert.Equal("batch:2:one,two", controller.SelectedPropertyObject);
	}

	[Fact]
	public void MultiPropertyAdapterIntersectsAndBroadcastsProperties()
	{
		var first = new EditableTarget { Text = "one", Width = 10 };
		var second = new EditableTarget { Text = "two", Width = 20 };
		var adapter = new DesignerMultiPropertyAdapter(new object[] { first, second });
		var properties = System.ComponentModel.TypeDescriptor.GetProperties(adapter);

		Assert.Contains(properties.Cast<System.ComponentModel.PropertyDescriptor>(), property => property.Name == nameof(EditableTarget.Text));
		Assert.Null(properties[nameof(EditableTarget.Text)]!.GetValue(adapter));
		properties[nameof(EditableTarget.Text)]!.SetValue(adapter, "shared");

		Assert.Equal("shared", first.Text);
		Assert.Equal("shared", second.Text);
		Assert.False(adapter.IsMixed(nameof(EditableTarget.Text)));
	}

	[Fact]
	public void MultiPropertyAdapterRollsBackEarlierTargetsWhenOneRejectsEdit()
	{
		var first = new EditableTarget { Text = "one" };
		var second = new EditableTarget { Text = "two", Reject = "invalid" };
		var adapter = new DesignerMultiPropertyAdapter(new object[] { first, second });

		Assert.True(adapter.IsMixed(nameof(EditableTarget.Text)));
		Assert.ThrowsAny<Exception>(() => TypeDescriptor.GetProperties(adapter)[nameof(EditableTarget.Text)]!.SetValue(adapter, "invalid"));
		Assert.Equal("one", first.Text);
		Assert.Equal("two", second.Text);
	}

	[Fact]
	public void MultiPropertyAdapterIntersectsAndBroadcastsEvents()
	{
		var first = new EditableTarget(); var second = new EditableTarget();
		var adapter = new DesignerMultiPropertyAdapter(new object[] { first, second });

		Assert.Contains(adapter.GetEvents().Cast<EventDescriptor>(), item => item.Name == nameof(EditableTarget.Click));
		((IEventBindingHost)adapter).BindEvent(nameof(EditableTarget.Click));
		Assert.Equal(nameof(EditableTarget.Click), first.BoundEvent);
		Assert.Equal(nameof(EditableTarget.Click), second.BoundEvent);
	}

	sealed class EditableTarget : IEventBindingHost
	{
		string text = "";
		public string Text { get => text; set { if (value == Reject) throw new InvalidOperationException("rejected"); text = value; } }
		public int Width { get; set; } public string? Reject { get; set; } public string? BoundEvent { get; private set; }
		public event EventHandler? Click;
		public void BindEvent(string eventName) => BoundEvent = eventName;
	}

	[Fact]
	public void RebuildRestoresSelectionByStableIdAndRecreatesPropertyAdapter()
	{
		var adapters = 0;
		var controller = new DesignerSelectionController(node => $"adapter-{node.Id}-{++adapters}");
		controller.UpdateTree(Tree("root", Tree("button")));
		Assert.True(controller.Select("button"));
		var original = controller.SelectedPropertyObject;

		controller.UpdateTree(Tree("root", Tree("button"), Tree("label")));

		Assert.Equal("button", controller.SelectedId);
		Assert.NotEqual(original, controller.SelectedPropertyObject);
		Assert.Equal(2, adapters);
	}

	[Fact]
	public void RebuildClearsRemovedSelectionAndRejectsUnknownId()
	{
		var controller = new DesignerSelectionController();
		controller.UpdateTree(Tree("root", Tree("button")));
		Assert.True(controller.Select("button"));
		controller.UpdateTree(Tree("root"));
		Assert.Null(controller.SelectedNode);
		Assert.False(controller.Select("missing"));
	}

	[Fact]
	public void RebuildRestoresPreferredSelectionWhenNodeReturns()
	{
		var controller = new DesignerSelectionController();
		controller.UpdateTree(Tree("root", Tree("button")));
		Assert.True(controller.Select("button"));
		controller.UpdateTree(Tree("root"));
		Assert.Null(controller.SelectedNode);

		controller.UpdateTree(Tree("root", Tree("button")));

		Assert.Equal("button", controller.SelectedId);
	}

	[Fact]
	public void SupportsForestsAndEmptyRootId()
	{
		var controller = new DesignerSelectionController();
		controller.UpdateRoots(new[] { Tree(""), Tree("dialog") });
		Assert.True(controller.Select(""));
		Assert.Equal("", controller.SelectedId);
		Assert.Equal(2, controller.Roots.Count);
	}

	static DesignerElementNode Tree(string id, params DesignerElementNode[] children) => new() { Id = id, Name = id, Type = "Control", Children = children.ToList() };
}

public sealed class DesignerCommandControllerTests
{
	[Fact]
	public void ExecuteHonorsEnablementAndInvalidatesState()
	{
		var enabled = false;
		var executions = 0;
		var changes = 0;
		var controller = new DesignerCommandController();
		controller.StateChanged += (_, _) => changes++;
		controller.Register("Undo", () => enabled, () => { executions++; return true; });

		Assert.False(controller.Execute("Undo"));
		enabled = true;
		controller.Invalidate();
		Assert.True(controller.Execute("Undo"));
		Assert.Equal(1, executions);
		Assert.Equal(3, changes);
	}

	[Fact]
	public void ExecuteRejectsReentrantCommand()
	{
		var nested = true;
		var controller = new DesignerCommandController();
		controller.Register("Delete", () => true, () => { nested = controller.Execute("Delete"); return true; });

		Assert.True(controller.Execute("Delete"));
		Assert.False(nested);
	}

	[Fact]
	public void StandardRegistrationExposesStableCapabilitySnapshot()
	{
		var canUndo = true;
		var controller = new DesignerCommandController();
		controller.RegisterStandard(() => canUndo, () => true, () => false, () => true,
			() => true, () => true);

		Assert.Equal(new[] { "Delete", "Redo", "Undo" }, controller.RegisteredCommands);
		Assert.True(controller.State(DesignerCommandNames.Undo).CanExecute);
		Assert.False(controller.State(DesignerCommandNames.Redo).CanExecute);
		Assert.True(controller.State(DesignerCommandNames.Delete).Registered);
		canUndo = false;
		Assert.False(controller.Snapshot().Single(state => state.Name == DesignerCommandNames.Undo).CanExecute);
	}
}

public sealed class DesignerToolboxControllerTests
{
	[Fact]
	public void FiltersAcrossCatalogueFieldsAndClearsHiddenSelection()
	{
		var controller = new DesignerToolboxController();
		controller.SetItems(new[] { Item("Button", "Controls"), Item("Grid", "Panels") });
		Assert.True(controller.Select("Button"));

		controller.Filter("panel");

		Assert.Equal("Grid", Assert.Single(controller.VisibleItems).TypeName);
		Assert.Null(controller.SelectedItem);

		controller.Filter("");
		Assert.Equal("Button", controller.SelectedItem!.TypeName);
	}

	[Fact]
	public void RebuildDeduplicatesAndRestoresSelectionByStableTypeName()
	{
		var controller = new DesignerToolboxController();
		controller.SetItems(new[] { Item("Button", "Controls"), Item("Button", "Other") });
		Assert.True(controller.Select("Button"));

		controller.SetItems(new[] { Item("Button", "Updated") });

		Assert.Single(controller.AllItems);
		Assert.Equal("Updated", controller.SelectedItem!.Category);
	}

	static DesignerToolboxItemInfo Item(string type, string category) => new() { Name = type, DisplayName = type, TypeName = type, Category = category };
}
