using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Presentation;

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
