using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class TableLayoutTracksEditModelTests
{
	static TableLayoutTracksEditModel Model() => new(new DesignerTableLayoutTracks {
		Rows = { new DesignerTableLayoutTrackEdit { SourceIndex = 0, SizeType = "Absolute", Value = 30 }, new DesignerTableLayoutTrackEdit { SourceIndex = 1, SizeType = "Percent", Value = 100 } },
		Columns = { new DesignerTableLayoutTrackEdit { SourceIndex = 0, SizeType = "Absolute", Value = 40 }, new DesignerTableLayoutTrackEdit { SourceIndex = 1, SizeType = "Percent", Value = 100 } }
	});

	[Fact]
	public void SelectingAnotherTrackCommitsTheDisplayedTrackFirst()
	{
		var model = Model();
		Assert.True(model.TrySelect(1, "Absolute", "45", out var error), error);
		Assert.Equal(45, model.Rows[0].Value);
		Assert.Equal(1, model.SelectedIndex);
		Assert.Equal("Percent", model.CurrentSizeType);
	}

	[Fact]
	public void InvalidSelectionKeepsTheDisplayedTrackSelectedAndUnchanged()
	{
		var model = Model();
		Assert.False(model.TrySelect(1, "Absolute", "not a number", out var error));
		Assert.NotEmpty(error);
		Assert.Equal(0, model.SelectedIndex);
		Assert.Equal(30, model.Rows[0].Value);
	}

	[Fact]
	public void SwitchingAxesCommitsOrKeepsTheExistingAxisOnValidationFailure()
	{
		var model = Model();
		Assert.False(model.TrySwitchAxis(false, "Absolute", "-1", out _));
		Assert.True(model.ShowingRows);
		Assert.True(model.TrySwitchAxis(false, "Absolute", "35", out var error), error);
		Assert.False(model.ShowingRows);
		Assert.Equal(35, model.Rows[0].Value);
		Assert.Equal(0, model.SelectedIndex);
	}

	[Fact]
	public void RejectsNonFiniteAndValuesOutsideTheHostFloatRange()
	{
		var model = Model();
		Assert.False(model.TryCommit("Absolute", "4e38", out _));
		Assert.False(model.TryCommit("Absolute", "NaN", out _));
		Assert.True(model.TryCommit("AutoSize", "ignored", out var error), error);
		Assert.Equal("AutoSize", model.Rows[0].SizeType);
		Assert.Equal(0, model.Rows[0].Value);
	}
}
