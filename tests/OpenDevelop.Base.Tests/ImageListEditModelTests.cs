using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class ImageListEditModelTests
{
	static readonly byte[] RedPixel = System.Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

	static DesignerImageListEntry Image(string key, params byte[] png) => new() { Key = key, Png = png.Length == 0 ? RedPixel : png };

	[Fact]
	public void StartsFromTheHostsImagesAndReadsTheSize()
	{
		var model = new ImageListEditModel(new[] { Image("a.png"), Image("") }, "24,32", "Depth8Bit");
		Assert.Equal(new[] { "a.png", "" }, model.Entries.Select(entry => entry.Key));
		Assert.Equal((24, 32, "Depth8Bit"), (model.Width, model.Height, model.ColorDepth));
		Assert.False(model.IsChanged);
		Assert.Equal((16, 16), (new ImageListEditModel(null, "garbage").Width, new ImageListEditModel(null, "").Height));
	}

	[Fact]
	public void AddKeepsKeysUniqueAndIgnoresEmptyPixels()
	{
		var model = new ImageListEditModel(new[] { Image("icon.png") });
		Assert.Equal(1, model.Add("icon.png", RedPixel));
		Assert.Equal(2, model.Add("icon.png", RedPixel));
		Assert.Equal(3, model.Add("", RedPixel));
		Assert.Equal(4, model.Add("", RedPixel));          // unnamed images never clash
		Assert.Equal(new[] { "icon.png", "icon (2).png", "icon (3).png", "", "" }, model.Entries.Select(entry => entry.Key));
		Assert.Equal(-1, model.Add("x.png", System.Array.Empty<byte>()));
		Assert.True(model.IsChanged);
	}

	[Fact]
	public void RenameRefusesADuplicateAndRemoveAndMoveStayInRange()
	{
		var model = new ImageListEditModel(new[] { Image("a"), Image("b"), Image("c") });
		Assert.False(model.Rename(0, "b"));
		Assert.True(model.Rename(0, "a"));              // renaming to its own key is fine
		Assert.True(model.Rename(0, ""));
		Assert.False(model.Rename(5, "z"));

		Assert.False(model.MoveUp(0));
		Assert.False(model.MoveDown(2));
		Assert.True(model.MoveDown(0));
		Assert.Equal(new[] { "b", "", "c" }, model.Entries.Select(entry => entry.Key));
		Assert.True(model.Remove(1));
		Assert.False(model.Remove(5));
		Assert.Equal(new[] { "b", "c" }, model.ToEntries().Select(entry => entry.Key));
	}

	[Fact]
	public void ChangeDetectionSeesPixelsKeysAndOrderButNotNoOps()
	{
		var other = System.Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
		var model = new ImageListEditModel(new[] { Image("a"), Image("b", other) });
		Assert.True(model.MoveDown(0));
		Assert.True(model.IsChanged);
		Assert.True(model.MoveUp(1));
		Assert.False(model.IsChanged);               // back to the start

		Assert.True(model.Rename(0, "a"));
		Assert.False(model.IsChanged);
		Assert.True(model.Rename(0, "A"));
		Assert.True(model.IsChanged);
	}

	[Fact]
	public void ScriptsReachTheSameOperationsAndRejectBadSteps()
	{
		var model = new ImageListEditModel(new[] { Image("a"), Image("b"), Image("c") });
		Assert.True(ImageListEditing.ApplyScript(model, "rename:0:first;down:0;remove:2"));
		Assert.Equal(new[] { "b", "first" }, model.Entries.Select(entry => entry.Key));
		Assert.False(ImageListEditing.ApplyScript(model, "cancel"));
		Assert.Throws<System.ArgumentException>(() => ImageListEditing.ApplyScript(model, "bogus:1"));
		Assert.Throws<System.ArgumentException>(() => ImageListEditing.ApplyScript(model, "remove:9"));
		Assert.Throws<System.ArgumentException>(() => ImageListEditing.ApplyScript(model, "add:/no/such/file.png"));
	}
}
