using System.ComponentModel;
using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class CollectionItemsEditModelTests
{
	static (int, string)[] Edits(CollectionItemsEditModel model)
		=> model.ToEdits().Select(edit => (edit.SourceIndex, edit.Text)).ToArray();

	[Fact]
	public void AnUntouchedListIsUnchangedAndMapsEveryItemToItself()
	{
		var model = new CollectionItemsEditModel(new[] { "A", "B" });
		Assert.False(model.IsChanged);
		Assert.Equal(new[] { (0, "A"), (1, "B") }, Edits(model));
	}

	[Fact]
	public void MoveRenameAddAndRemoveKeepEachItemsOriginalIndex()
	{
		var model = new CollectionItemsEditModel(new[] { "A", "B", "C" });
		Assert.True(model.MoveDown(0));       // B A C
		Assert.True(model.Rename(1, "A!"));    // B A! C
		Assert.Equal(1, model.Add("New", 0));  // B New A! C
		Assert.True(model.Remove(3));          // B New A!
		Assert.True(model.IsChanged);
		Assert.Equal(new[] { (1, "B"), (-1, "New"), (0, "A!") }, Edits(model));
	}

	[Fact]
	public void MovingPastEitherEndOrRemovingOutOfRangeIsRejected()
	{
		var model = new CollectionItemsEditModel(new[] { "A", "B" });
		Assert.False(model.MoveUp(0));
		Assert.False(model.MoveDown(1));
		Assert.False(model.Remove(2));
		Assert.False(model.Rename(-1, "X"));
		Assert.False(model.IsChanged);
	}

	[Fact]
	public void AddWithoutAnAnchorAppendsAndRemovingEverythingIsAChange()
	{
		var model = new CollectionItemsEditModel(new[] { "A" });
		Assert.Equal(1, model.Add("B"));
		Assert.Equal(new[] { (0, "A"), (-1, "B") }, Edits(model));
		model.Remove(0);
		model.Remove(0);
		Assert.True(model.IsChanged);
		Assert.Empty(model.ToEdits());
	}

	[Fact]
	public void ANullItemTextIsAnUnchangedEmptyItem()
	{
		var model = new CollectionItemsEditModel(new string?[] { "A", null }!);
		Assert.False(model.IsChanged);
		Assert.Equal(new[] { (0, "A"), (1, "") }, Edits(model));
	}

	[Fact]
	public void MovingAnItemAwayAndBackIsNotAChange()
	{
		var model = new CollectionItemsEditModel(new[] { "A", "B" });
		model.MoveDown(0);
		model.MoveUp(1);
		Assert.False(model.IsChanged);
	}
}

public sealed class CollectionItemsEditingScriptTests
{
	[Fact]
	public void AScriptAppliesEachStepInOrder()
	{
		var model = new CollectionItemsEditModel(new[] { "A", "B" });
		Assert.True(CollectionItemsEditing.ApplyScript(model, "down:0;rename:1:A!;add:1:C:with:colons"));
		Assert.Equal(new[] { "B", "A!", "C:with:colons" }, model.Entries.Select(entry => entry.Text));
	}

	[Fact]
	public void CancelStopsAndRejectedOrUnknownStepsThrow()
	{
		var model = new CollectionItemsEditModel(new[] { "A" });
		Assert.False(CollectionItemsEditing.ApplyScript(model, "rename:0:X;cancel;rename:0:Y"));
		Assert.Equal("X", model.Entries[0].Text);
		Assert.Throws<System.ArgumentException>(() => CollectionItemsEditing.ApplyScript(model, "up:0"));
		Assert.Throws<System.ArgumentException>(() => CollectionItemsEditing.ApplyScript(model, "shuffle:0"));
	}

	[Fact]
	public void DataGridKindScriptsPreserveExistingKindsAndCreateRequestedKinds()
	{
		var model = new CollectionItemsEditModel(new[] { "Name", "Active" }, new[] { "Text", "CheckBox" }, true);
		Assert.True(CollectionItemsEditing.ApplyScript(model, "add-checkbox:1:New enabled;add-combo:2:New role"));
		Assert.Equal(new[] { "Text", "CheckBox", "CheckBox", "ComboBox" }, model.ToEdits().Select(edit => edit.Kind));
	}

	[Fact]
	public void EditUsesCanonicalPropertyNameWhenTheDescriptorProvidesOne()
	{
		var host = new CapturingItemsHost();
		var descriptor = new CanonicalItemsDescriptor("Text_Bindings", "Text.Bindings");
		CollectionItemsEditing.TestEdit = model => {
			model.Rename(0, "Updated");
			return true;
		};
		try {
			CollectionItemsEditing.Edit(host, descriptor, null, exception => throw exception);
		} finally {
			CollectionItemsEditing.TestEdit = null;
		}
		Assert.Equal("Text.Bindings", host.PropertyName);
		Assert.Equal(new[] { (0, "Updated") }, host.Items);
	}

	sealed class CapturingItemsHost : IItemsCollectionEditorHost
	{
		public string? PropertyName { get; private set; }
		public (int, string)[] Items { get; private set; } = Array.Empty<(int, string)>();
		public void SetItems(string propertyName, System.Collections.Generic.IReadOnlyList<DesignerCollectionItemEdit> items)
		{
			PropertyName = propertyName;
			Items = items.Select(item => (item.SourceIndex, item.Text)).ToArray();
		}
	}

	sealed class CanonicalItemsDescriptor : PropertyDescriptor, IItemsCollectionProperty, IItemsCollectionPropertyName
	{
		public CanonicalItemsDescriptor(string name, string propertyName) : base(name, null) => PropertyName = propertyName;
		public string PropertyName { get; }
		public System.Collections.Generic.IReadOnlyList<string> CollectionItems => new[] { "Original" };
		public System.Collections.Generic.IReadOnlyList<string> CollectionItemKinds => System.Array.Empty<string>();
		public bool SupportsDataGridColumnKinds => false;
		public override Type ComponentType => typeof(CapturingItemsHost);
		public override Type PropertyType => typeof(string);
		public override bool IsReadOnly => true;
		public override bool CanResetValue(object component) => false;
		public override object? GetValue(object component) => null;
		public override void ResetValue(object component) { }
		public override void SetValue(object component, object? value) { }
		public override bool ShouldSerializeValue(object component) => false;
	}
}
