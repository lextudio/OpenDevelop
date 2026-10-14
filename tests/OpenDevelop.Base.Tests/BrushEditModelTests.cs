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
}
