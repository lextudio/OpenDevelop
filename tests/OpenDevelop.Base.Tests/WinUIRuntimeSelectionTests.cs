using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class WinUIRuntimeSelectionTests
{
    [Theory]
    [InlineData(null, null, "surface")]
    [InlineData("", "", "surface")]
    [InlineData("  ", "\t", "surface")]
    [InlineData(null, "microsoft", "microsoft")]
    [InlineData(null, " Uno ", "uno")]
    [InlineData(null, "SURFACE", "surface")]
    [InlineData("microsoft", "surface", "microsoft")]
    [InlineData("uno", "microsoft", "uno")]
    [InlineData("surface", "microsoft", "surface")]
    [InlineData(" ", "microsoft", "microsoft")]
    public void Resolve_PrefersEnvironment_ThenStored_ThenDefault(string? env, string? stored, string expected)
    {
        Assert.Equal(expected, WinUIRuntimeSelection.Resolve(env, stored, out var unrecognized));
        Assert.Null(unrecognized);
    }

    [Theory]
    [InlineData("progpu", null, "progpu")]
    [InlineData(null, "nonsense", "nonsense")]
    [InlineData("bogus", "microsoft", "bogus")]
    public void Resolve_UnrecognizedValue_FallsBackToDefaultAndReportsIt(string? env, string? stored, string reported)
    {
        Assert.Equal("surface", WinUIRuntimeSelection.Resolve(env, stored, out var unrecognized));
        Assert.Equal(reported, unrecognized);
    }

    [Theory]
    [InlineData(null, "surface")]
    [InlineData("surface", "surface")]
    [InlineData("microsoft", "microsoft")]
    [InlineData(" Microsoft ", "microsoft")]
    [InlineData("uno", "surface")]
    [InlineData("nonsense", "surface")]
    public void ToOptionsChoice_ShowsOnlyWhatThePageOffers(string? stored, string expected)
        => Assert.Equal(expected, WinUIRuntimeSelection.ToOptionsChoice(stored));

    [Theory]
    // Unchanged choice: nothing is written, so a stored "uno" survives accepting the dialog.
    [InlineData("uno", "surface", null)]
    [InlineData("nonsense", "surface", null)]
    [InlineData(null, "surface", null)]
    [InlineData("surface", "surface", null)]
    [InlineData("microsoft", "microsoft", null)]
    // Changed choice: written.
    [InlineData("uno", "microsoft", "microsoft")]
    [InlineData(null, "microsoft", "microsoft")]
    [InlineData("microsoft", "surface", "surface")]
    public void ValueToStore_WritesOnlyAChangedChoice(string? stored, string selected, string? expected)
        => Assert.Equal(expected, WinUIRuntimeSelection.ValueToStore(stored, selected));

    [Theory]
    [InlineData("uno", "surface")]
    [InlineData("uno", "microsoft")]
    [InlineData(null, "microsoft")]
    [InlineData("microsoft", "surface")]
    public void OptionsRoundTrip_SelectThenSave_ResolvesToWhatWasShownOrChanged(string? stored, string selected)
    {
        // The page shows ToOptionsChoice(stored); if the user keeps it, the stored value must still resolve as before.
        var shown = WinUIRuntimeSelection.ToOptionsChoice(stored);
        var before = WinUIRuntimeSelection.Resolve(null, stored, out _);
        var afterKeep = WinUIRuntimeSelection.ValueToStore(stored, shown) ?? stored;
        Assert.Equal(before, WinUIRuntimeSelection.Resolve(null, afterKeep, out _));

        var afterChange = WinUIRuntimeSelection.ValueToStore(stored, selected) ?? stored;
        var expected = selected == shown ? before : selected;
        Assert.Equal(expected, WinUIRuntimeSelection.Resolve(null, afterChange, out _));
    }
}
