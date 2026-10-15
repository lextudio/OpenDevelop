using System;
using System.Linq;
using System.Windows.Media;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class BrushEditModelTests
{
	static DesignerBrushInfo Gradient() => new() {
		Kind = BrushEditModel.LinearKind,
		Stops = { new DesignerGradientStopInfo { Color = "#FFFF0000", Offset = 0 }, new DesignerGradientStopInfo { Color = "#FF0000FF", Offset = 1 } }
	};

	[Fact]
	public void SolidToGradientStartsFromTheCurrentColorAndBackKeepsTheSelectedStop()
	{
		var model = new BrushEditModel(new DesignerBrushInfo { Kind = BrushEditModel.SolidKind, Color = "#FF00FF00" });
		Assert.False(model.IsChanged);

		model.SetKind(BrushEditModel.LinearKind);

		Assert.Equal(new[] { (Colors.Lime, 0.0), (Colors.White, 1.0) }, model.Stops.Select(stop => (stop.Color, stop.Offset)));
		model.SelectStop(1);
		model.SelectedColor = Colors.Navy;
		model.SetKind(BrushEditModel.SolidKind);
		Assert.Equal("#FF000080", model.ToInfo().Color);
		Assert.True(model.IsChanged);
	}

	[Fact]
	public void PickingAColorOnNoBrushMakesItSolid()
	{
		var model = new BrushEditModel(null);
		Assert.Equal(BrushEditModel.NullKind, model.Kind);

		model.SelectedColor = Colors.Red;

		Assert.Equal(BrushEditModel.SolidKind, model.Kind);
		Assert.True(model.IsChanged);
	}

	[Fact]
	public void StopsAddWithTheGradientsColorMoveInOrderReverseAndKeepTwo()
	{
		var model = new BrushEditModel(Gradient());

		var added = model.AddStop(0.5);

		Assert.Equal(1, added);
		Assert.Equal(Color.FromArgb(255, 128, 0, 128), model.Stops[1].Color);
		Assert.True(model.SetStopOffset(1, 0.9));
		Assert.Equal(new[] { 0.0, 0.9, 1.0 }, model.Stops.Select(stop => stop.Offset));
		Assert.True(model.SetStopOffset(1, 2));
		Assert.Equal(new[] { 0.0, 1.0, 1.0 }, model.Stops.Select(stop => stop.Offset));
		model.Reverse();
		Assert.Equal(Colors.Red, model.Stops[^1].Color);
		Assert.True(model.RemoveStop(0));
		Assert.False(model.RemoveStop(0));
		Assert.Equal(2, model.Stops.Count);
	}

	[Fact]
	public void ScriptEditsTheModelAndCancelOrBadStepsStop()
	{
		var model = new BrushEditModel(Gradient());

		Assert.True(BrushEditing.ApplyScript(model, "kind:RadialGradient;select:1;color:#FF00FF00;radius:0.8,0.6;spread:Reflect;opacity:0.5"));

		var info = model.ToInfo();
		Assert.Equal(BrushEditModel.RadialKind, info.Kind);
		Assert.Equal("#FF00FF00", info.Stops[1].Color);
		Assert.Equal((0.8, 0.6), (info.RadiusX, info.RadiusY));
		Assert.Equal("Reflect", info.SpreadMethod);
		Assert.Equal(0.5, info.Opacity);
		Assert.False(BrushEditing.ApplyScript(new BrushEditModel(Gradient()), "reverse;cancel"));
		Assert.Throws<ArgumentException>(() => BrushEditing.ApplyScript(new BrushEditModel(Gradient()), "remove:0"));
		Assert.Throws<ArgumentException>(() => BrushEditing.ApplyScript(new BrushEditModel(Gradient()), "paint:red"));
	}

	[Fact]
	public void HsvRoundTripsPrimaryAndMixedColors()
	{
		foreach (var color in new[] { Colors.Red, Colors.Lime, Colors.Blue, Color.FromRgb(12, 200, 99), Colors.Gray, Colors.Black }) {
			var (hue, saturation, value) = BrushEditorDialog.ToHsv(color);
			Assert.Equal(color, BrushEditorDialog.FromHsv(hue, saturation, value, 255));
		}
	}

	[Fact]
	public void ResourcesTabSelectsOnlyOfferedKeysAndAColorLeavesTheResource()
	{
		var model = new BrushEditModel(new DesignerBrushInfo { Kind = BrushEditModel.SolidKind, Color = "#FFFF0000" }, new[] { "Accent", "Fade" });

		Assert.False(model.SelectResource("Missing"));
		Assert.True(BrushEditing.ApplyScript(model, "resource:Fade"));

		Assert.Equal((BrushEditModel.ResourceKind, "Fade"), (model.ToInfo().Kind, model.ToInfo().ResourceKey));
		Assert.True(model.IsChanged);
		model.SelectedColor = Colors.Blue;
		Assert.Equal(BrushEditModel.SolidKind, model.Kind);
		Assert.Equal("", model.ToInfo().ResourceKey);
	}

	[Fact]
	public void ResourcesTabOffersSystemBrushesAfterTheDocumentKeys()
	{
		var model = new BrushEditModel(new DesignerBrushInfo { Kind = BrushEditModel.SystemKind, ResourceKey = "ControlBrushKey" }, new[] { "Accent" });

		Assert.Equal("SystemColors.ControlBrushKey", model.SelectedResourceEntry);
		Assert.Equal("Accent", model.ResourceEntries[0]);
		Assert.Contains("SystemColors.HighlightBrushKey", model.ResourceEntries);
		Assert.DoesNotContain(model.ResourceEntries, entry => entry.EndsWith("ColorKey", StringComparison.Ordinal));
		Assert.False(model.IsChanged);

		Assert.False(model.SelectSystemResource("ControlColorKey"));
		Assert.True(model.SelectResourceEntry("SystemColors.HighlightBrushKey"));
		Assert.Equal((BrushEditModel.SystemKind, "HighlightBrushKey"), (model.ToInfo().Kind, model.ToInfo().ResourceKey));
		Assert.True(model.SelectResourceEntry("Accent"));
		Assert.Equal((BrushEditModel.ResourceKind, "Accent"), (model.ToInfo().Kind, model.ToInfo().ResourceKey));
		Assert.True(BrushEditing.ApplyScript(model, "system:WindowBrushKey"));
		// SystemColors brushes are frozen; the preview must not try to change one.
		_ = model.ToPreviewBrush();
		Assert.Equal("SystemColors.WindowBrushKey", model.SelectedResourceEntry);
		model.SelectedColor = Colors.Blue;
		Assert.Equal((BrushEditModel.SolidKind, ""), (model.Kind, model.ToInfo().ResourceKey));
	}

	[Fact]
	public void ImageTabWritesAnImageBrushAndAColorLeavesIt()
	{
		var model = new BrushEditModel(new DesignerBrushInfo { Kind = BrushEditModel.ImageKind, ImageSource = "images/a.png", Stretch = "Uniform" },
			null, new[] { "images/a.png", "images/b.png" });
		Assert.Equal(new[] { "images/a.png", "images/b.png" }, model.ImageSources);
		Assert.False(model.IsChanged);

		Assert.False(model.SetImage("  "));
		Assert.False(model.SetStretch("Sideways"));
		Assert.True(BrushEditing.ApplyScript(model, "image:images/b.png;stretch:Fill"));
		var info = model.ToInfo();
		// Fill is the default and is not written.
		Assert.Equal((BrushEditModel.ImageKind, "images/b.png", ""), (info.Kind, info.ImageSource, info.Stretch));
		Assert.True(model.IsChanged);

		model.SelectedColor = Colors.Blue;
		Assert.Equal(BrushEditModel.SolidKind, model.Kind);
		Assert.Equal("", model.ToInfo().ImageSource);
	}

	[Fact]
	public void ResourcesTabOffersColorResourcesAsSolidBrushes()
	{
		var model = new BrushEditModel(new DesignerBrushInfo { Kind = BrushEditModel.ColorResourceKind, ResourceKey = "Base" },
			new[] { "Fade" }, null, new[] { "Accent", "Base" });
		Assert.Equal("Color/Base", model.SelectedResourceEntry);
		Assert.Equal(new[] { "Fade", "Color/Accent", "Color/Base" }, model.ResourceEntries.Take(3));
		Assert.False(model.IsChanged);
		Assert.False(model.SelectColorResource("Fade"));
		Assert.True(model.SelectResourceEntry("Color/Accent"));
		Assert.Equal((BrushEditModel.ColorResourceKind, "Accent"), (model.ToInfo().Kind, model.ToInfo().ResourceKey));
		Assert.True(BrushEditing.ApplyScript(model, "resource:Fade"));
		Assert.Equal((BrushEditModel.ResourceKind, "Fade"), (model.ToInfo().Kind, model.ToInfo().ResourceKey));
		model.SelectedColor = Colors.Blue;
		Assert.Equal(BrushEditModel.SolidKind, model.Kind);
	}

	[Fact]
	public void ImageTileSettingsRoundTripAndRejectInvalidValues()
	{
		var model = new BrushEditModel(new DesignerBrushInfo { Kind = BrushEditModel.ImageKind, ImageSource = "a.png", TileMode = "Tile", Viewport = "0,0,0.5,0.5" });
		Assert.Equal(("Tile", "0,0,0.5,0.5"), (model.TileMode, model.Viewport));
		Assert.False(model.IsChanged);

		Assert.True(model.SetTileSetting("AlignmentX", "Left"));
		Assert.True(model.SetTileSetting("ViewboxUnits", "Absolute"));
		Assert.True(model.SetTileSetting("Viewbox", " 0,0,10,10 "));
		// The defaults are stored as empty so they are never written.
		Assert.True(model.SetTileSetting("TileMode", "None"));
		Assert.True(model.SetTileSetting("AlignmentY", "Center"));
		var info = model.ToInfo();
		Assert.Equal(("", "Left", "", "0,0,0.5,0.5", "", "0,0,10,10", "Absolute"),
			(info.TileMode, info.AlignmentX, info.AlignmentY, info.Viewport, info.ViewportUnits, info.Viewbox, info.ViewboxUnits));
		Assert.True(model.IsChanged);

		Assert.False(model.SetTileSetting("TileMode", "Bogus"));
		Assert.False(model.SetTileSetting("Viewport", "1,2,3"));
		Assert.False(model.SetTileSetting("Viewport", "0,0,-1,1"));
		Assert.False(model.SetTileSetting("Nonsense", "x"));
		Assert.Equal("0,0,0.5,0.5", model.Viewport);
		// A color leaves the image brush, and an edit script reaches the same setters.
		Assert.True(BrushEditing.ApplyScript(model, "tile:TileMode:FlipX;tile:Viewport:0,0,2,2"));
		Assert.Equal(("FlipX", "0,0,2,2"), (model.TileMode, model.Viewport));
	}

	[Fact]
	public void ContentTileBrushesKeepTheirKindAndOnlyEditTileSettings()
	{
		var model = new BrushEditModel(new DesignerBrushInfo { Kind = BrushEditModel.DrawingKind, TileMode = "Tile", Stretch = "Uniform", Opacity = 0.5 });
		Assert.True(model.IsContentTileBrush);
		Assert.True(model.SetTileSetting("Viewport", "0,0,4,4"));
		Assert.True(model.SetStretch("None"));
		var info = model.ToInfo();
		Assert.Equal((BrushEditModel.DrawingKind, "Tile", "0,0,4,4", "None", 0.5, ""), (info.Kind, info.TileMode, info.Viewport, info.Stretch, info.Opacity, info.ImageSource));
		Assert.True(model.IsChanged);
		// Picking a color replaces the drawing with a solid brush, as any non-solid kind does.
		model.SelectedColor = System.Windows.Media.Colors.Red;
		Assert.Equal(BrushEditModel.SolidKind, model.Kind);
	}

	[Fact]
	public void BrushTransformsRoundTripAreCopiedAndOnlyApplyToBrushesWithPixels()
	{
		var source = new[] { new DesignerTransformOp { Type = "Rotate", Angle = 30 } };
		var model = new BrushEditModel(new DesignerBrushInfo { Kind = BrushEditModel.SolidKind, Color = "#FFFF0000", RelativeTransform = source.ToList() });
		Assert.Equal(30d, Assert.Single(model.RelativeTransform).Angle);
		Assert.False(model.IsChanged);
		Assert.True(model.CanTransform);

		Assert.True(model.SetTransform("Transform", new[] { new DesignerTransformOp { Type = "Translate", X = 5 } }));
		Assert.True(model.IsChanged);
		var info = model.ToInfo();
		Assert.Equal(5d, Assert.Single(info.Transform).X);
		Assert.Equal(30d, Assert.Single(info.RelativeTransform).Angle);
		// The model and the caller's list never share operations.
		source[0].Angle = 99;
		Assert.Equal(30d, model.RelativeTransform[0].Angle);

		Assert.False(model.SetTransform("Bogus", System.Array.Empty<DesignerTransformOp>()));
		Assert.False(model.SetTransform("Transform", new[] { new DesignerTransformOp { Type = "Matrix" } }));
		// No brush, and references, have no transform of their own and drop them from the written info.
		model.SetKind(BrushEditModel.NullKind);
		Assert.False(model.CanTransform);
		Assert.False(model.SetTransform("Transform", System.Array.Empty<DesignerTransformOp>()));
		Assert.Empty(model.ToInfo().Transform);
	}

	[Fact]
	public void TransformScriptStepEditsAThroughTheTransformEditorSteps()
	{
		var model = new BrushEditModel(new DesignerBrushInfo { Kind = BrushEditModel.LinearKind, Stops = { new DesignerGradientStopInfo { Color = "#FFFF0000" }, new DesignerGradientStopInfo { Color = "#FF0000FF", Offset = 1 } } });
		Assert.True(BrushEditing.ApplyScript(model, "transform:RelativeTransform:add:Rotate|set:0:Angle:45|add:Scale|set:1:ScaleX:2"));
		Assert.Equal(new[] { "Rotate", "Scale" }, model.RelativeTransform.Select(op => op.Type));
		Assert.Equal((45d, 2d), (model.RelativeTransform[0].Angle, model.RelativeTransform[1].ScaleX));
		Assert.Empty(model.Transform);
		Assert.Throws<System.ArgumentException>(() => BrushEditing.ApplyScript(model, "transform:Transform:add:Matrix"));
		Assert.Throws<System.ArgumentException>(() => BrushEditing.ApplyScript(model, "transform:Bogus:add:Rotate"));
	}
}
