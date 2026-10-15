using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class TransformEditModelTests
{
	[Fact]
	public void AddRemoveReorderAndSetFieldsProduceTheOperationList()
	{
		var model = new TransformEditModel(new[] { new DesignerTransformOp { Type = "Rotate", Angle = 30 } });
		Assert.False(model.IsChanged);
		Assert.Equal(0, model.SelectedIndex);

		Assert.True(model.Add("Scale"));
		Assert.Equal(1, model.SelectedIndex);
		Assert.True(model.SetField(1, "ScaleX", 2));
		Assert.True(model.SetField(1, "ScaleY", 0.5));
		Assert.True(model.Add("Translate"));
		Assert.True(model.Move(2, -1));
		Assert.Equal(new[] { "Rotate", "Translate", "Scale" }, model.ToOps().Select(op => op.Type));
		Assert.True(model.IsChanged);

		Assert.True(model.Remove(0));
		Assert.Equal(new[] { "Translate", "Scale" }, model.ToOps().Select(op => op.Type));
		Assert.Equal((2d, 0.5d), (model.ToOps()[1].ScaleX, model.ToOps()[1].ScaleY));
		Assert.Equal("Scale(2, 0.5)", TransformEditModel.Describe(model.ToOps()[1]));
	}

	[Fact]
	public void InvalidEditsAreRejectedAndChangeNothing()
	{
		var model = new TransformEditModel(null);
		Assert.Equal(-1, model.SelectedIndex);
		Assert.False(model.Add("Matrix"));
		Assert.False(model.Remove(0));
		Assert.False(model.Move(0, 1));
		Assert.True(model.Add("Rotate"));
		Assert.False(model.SetField(0, "ScaleX", 2));            // not a Rotate field
		Assert.False(model.SetField(0, "Angle", double.NaN));    // not finite
		Assert.False(model.SetField(3, "Angle", 1));              // no such operation
		Assert.False(model.Move(0, 2));
		Assert.Equal(0d, model.GetField(0, "Angle"));
	}

	[Fact]
	public void TheOriginalListIsCopiedNotShared()
	{
		var source = new[] { new DesignerTransformOp { Type = "Translate", X = 4 } };
		var model = new TransformEditModel(source);
		model.SetField(0, "X", 9);
		Assert.Equal(4d, source[0].X);
		Assert.Equal(9d, model.ToOps()[0].X);
	}

	[Fact]
	public void EditScriptsReachTheSameOperationsAndRejectBadSteps()
	{
		var model = new TransformEditModel(null);
		Assert.True(TransformEditing.ApplyScript(model, "add:Rotate;set:0:Angle:45;add:Skew;set:1:AngleX:10;up:1"));
		Assert.Equal(new[] { "Skew", "Rotate" }, model.ToOps().Select(op => op.Type));
		Assert.Equal(45d, model.ToOps()[1].Angle);
		Assert.False(TransformEditing.ApplyScript(model, "cancel"));
		Assert.Throws<System.ArgumentException>(() => TransformEditing.ApplyScript(model, "add:Matrix"));
		Assert.Throws<System.ArgumentException>(() => TransformEditing.ApplyScript(model, "bogus:1"));
		Assert.Throws<System.ArgumentException>(() => TransformEditing.ApplyScript(model, "set:0:ScaleX:2"));
	}
}
