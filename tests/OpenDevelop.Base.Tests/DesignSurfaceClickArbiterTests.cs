using System.Windows;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Pins down design-surface click arbitration - who owns a left press: an adorner glyph drawn over
/// the selection, a component underneath it, or empty canvas.
///
/// Every case here corresponds to a bug that actually shipped while this logic lived inline in
/// RemoteFormsDesignerControl.OnMouseLeftButtonDown, where the only way to exercise it was a full
/// build-launch-click-by-hand cycle. Three regressions came out of it in a row, each one caused by
/// the fix for the previous. See DesignSurfaceClickArbiter's own remarks.
///
/// The fixture models this repo's tests/fixtures/TabControlFixture: a form holding a TabControl
/// with two pages, whose children DELIBERATELY share bounds - both pages occupy the same rect, so
/// button1 (page 1) and button2 (page 2) overlap exactly. That overlap is what made these bugs so
/// confusing to diagnose by eye, and it is why visibility has to be part of the arbitration.
/// </summary>
public class DesignSurfaceClickArbiterTests
{
	const string Root = "MainForm";

	/// <param name="activePage">Which TabPage is currently selected, i.e. actually on screen.</param>
	static List<DesignSurfaceClickCandidate> Fixture(string activePage = "tabPage1")
	{
		var page1Visible = activePage == "tabPage1";
		return new List<DesignSurfaceClickCandidate> {
			// The design root: never a click candidate, whatever its bounds say.
			new(Root, null, new Rect(0, 0, 400, 260), true),
			new("tabControl1", Root, new Rect(12, 12, 360, 220), true),
			// Both pages occupy the SAME rect inside the TabControl - only one is ever visible.
			new("tabPage1", "tabControl1", new Rect(16, 36, 352, 192), page1Visible),
			new("tabPage2", "tabControl1", new Rect(16, 36, 352, 192), !page1Visible),
			// ...and so their children overlap exactly.
			new("button1", "tabPage1", new Rect(36, 56, 100, 30), page1Visible),
			new("button2", "tabPage2", new Rect(36, 56, 100, 30), !page1Visible),
			new("label1", "tabPage2", new Rect(36, 96, 200, 23), !page1Visible)
		};
	}

	static readonly Point OnButton = new(60, 70);          // inside button1/button2's shared rect
	static readonly Point OnPageBackground = new(300, 200); // inside the active page, on no child
	static readonly Point OnEmptyForm = new(390, 250);      // on the form itself, outside everything

	// ---------------------------------------------------------------- drag the current selection

	/// <summary>
	/// REGRESSION (move-dragging broken outright): pressing on the already-selected control to drag
	/// it must be left to the move thumb. The selected component's own bounds contain the press, so
	/// an arbiter asking merely "did this land on some component?" concluded "drill through", tore
	/// the thumb's mouse capture away, and the drag died before its first delta.
	/// </summary>
	[Fact]
	public void PressOnAdornerOverTheSelectionItself_LetsTheAdornerHandleIt()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), OnButton, selectedName: "button1", pressOriginatedOnAdorner: true);

		Assert.Equal(DesignSurfaceClickAction.LetAdornerHandle, decision.Action);
		Assert.False(decision.ReleaseAdornerCapture);
	}

	/// <summary>
	/// REGRESSION (move-dragging broken for NESTED controls only): every ANCESTOR of the selection
	/// contains the press too - tabPage1 contains button1, tabControl1 contains tabPage1 - so an
	/// arbiter that excluded only the selection itself still saw "something else is under the
	/// pointer" and killed the drag. Controls sitting directly on the form kept working, because
	/// their sole containing ancestor is the root, which is never a candidate; that asymmetry is
	/// exactly what made the bug look arbitrary.
	/// </summary>
	/// <param name="x">Deliberately a point inside the selection AND inside every one of its
	/// ancestors, but NOT inside any deeper child of it - a press on a child is a drill-through by
	/// design (covered below), so it cannot demonstrate anything about ancestors.</param>
	[Theory]
	[InlineData("button1", 60, 70)]      // nested two deep (parent tabPage1, grandparent tabControl1)
	[InlineData("tabPage1", 300, 200)]   // nested one deep: page background, clear of button1
	[InlineData("tabControl1", 14, 20)]  // top level: its own header strip, clear of any page
	public void PressOnAdornerOverTheSelection_IsNeverStolenByAnAncestorContainingThePoint(
		string selected, double x, double y)
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), new Point(x, y), selectedName: selected, pressOriginatedOnAdorner: true);

		Assert.Equal(DesignSurfaceClickAction.LetAdornerHandle, decision.Action);
	}

	// ------------------------------------------------------------------- drill into a child

	/// <summary>
	/// REGRESSION (selection stuck on the container): with a container selected, its move thumb
	/// covers every child inside it, so a press on a child was swallowed as a drag-start and the
	/// selection never moved off the container. A child is MORE SPECIFIC than the selection, so it
	/// must win - matching real VS, where clicking a control inside an already-selected container
	/// selects that control.
	/// </summary>
	[Fact]
	public void PressOnAdornerOverAChildOfTheSelection_DrillsThroughToSelectIt()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), OnButton, selectedName: "tabControl1", pressOriginatedOnAdorner: true);

		Assert.Equal(DesignSurfaceClickAction.SelectComponent, decision.Action);
		// The thumb may already have captured the mouse as this press bubbled through it; the
		// caller has to release that, or a drag runs on top of the selection change.
		Assert.True(decision.ReleaseAdornerCapture);
	}

	[Fact]
	public void PressOnAdornerOverAGrandchildOfTheSelection_DrillsThrough()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), OnButton, selectedName: "tabPage1", pressOriginatedOnAdorner: true);

		Assert.Equal(DesignSurfaceClickAction.SelectComponent, decision.Action);
		Assert.True(decision.ReleaseAdornerCapture);
	}

	// ------------------------------------------------------------------------- visibility

	/// <summary>
	/// The bug that cost the most to diagnose: a control on a non-selected TabPage reports the
	/// bounds it WOULD occupy, overlapping the page that IS showing. Counting it makes a press
	/// resolve to something invisible - which is how a click aimed at what looked like a control
	/// ended up selecting its enclosing TabPage instead (the child process's own hit-test correctly
	/// honours visibility and refuses to resolve to a hidden control).
	/// </summary>
	[Fact]
	public void HiddenPagesChildren_AreNotClickCandidatesEvenThoughTheirBoundsOverlap()
	{
		var candidates = Fixture(activePage: "tabPage1");

		// button2/label1 live on the hidden page but overlap button1 exactly.
		Assert.True(DesignSurfaceClickArbiter.HitsAnyCandidate(candidates, OnButton));

		// With button1 (the VISIBLE one) selected, nothing more specific is under the pointer -
		// the hidden button2 at the same coordinates must not count as a drill-through target.
		var decision = DesignSurfaceClickArbiter.Decide(
			candidates, OnButton, selectedName: "button1", pressOriginatedOnAdorner: true);
		Assert.Equal(DesignSurfaceClickAction.LetAdornerHandle, decision.Action);
	}

	/// <summary>Once the other page becomes active the same coordinates flip ownership - so the
	/// arbitration follows a tab switch rather than being fixed at load.</summary>
	[Fact]
	public void SwitchingTheActivePage_FlipsWhichOverlappingChildOwnsThePoint()
	{
		var onPage2 = Fixture(activePage: "tabPage2");

		// button1 is now hidden, so it cannot be dragged and cannot be drilled into...
		Assert.True(DesignSurfaceClickArbiter.HasCandidateMoreSpecificThanSelection(
			onPage2, OnButton, selectedName: "button1"));
		// ...while button2 - now the visible one at those coordinates - owns its own drag.
		Assert.False(DesignSurfaceClickArbiter.HasCandidateMoreSpecificThanSelection(
			onPage2, OnButton, selectedName: "button2"));
	}

	/// <summary>A press where only hidden components sit must be treated as empty canvas (marquee),
	/// not as a click on a component that cannot be seen.</summary>
	[Fact]
	public void PressWhereOnlyHiddenComponentsSit_StartsAMarquee()
	{
		var candidates = new List<DesignSurfaceClickCandidate> {
			new(Root, null, new Rect(0, 0, 400, 260), true),
			new("hiddenPanel", Root, new Rect(10, 10, 100, 100), false)
		};

		var decision = DesignSurfaceClickArbiter.Decide(
			candidates, new Point(50, 50), selectedName: null, pressOriginatedOnAdorner: false);

		Assert.Equal(DesignSurfaceClickAction.StartMarquee, decision.Action);
	}

	// --------------------------------------------------------------------------- marquee

	/// <summary>The design ROOT is never a click candidate: a press on empty form background must
	/// start a rubber band, not count as "you clicked the form" - otherwise marquee selection is
	/// impossible, since the root's bounds cover the entire surface.</summary>
	[Fact]
	public void PressOnEmptyFormBackground_StartsAMarquee()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), OnEmptyForm, selectedName: null, pressOriginatedOnAdorner: false);

		Assert.Equal(DesignSurfaceClickAction.StartMarquee, decision.Action);
		Assert.False(decision.ReleaseAdornerCapture);
	}

	[Fact]
	public void PressOnAComponent_SelectsItRatherThanStartingAMarquee()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), OnButton, selectedName: null, pressOriginatedOnAdorner: false);

		Assert.Equal(DesignSurfaceClickAction.SelectComponent, decision.Action);
	}

	/// <summary>A press inside the active TabPage but on none of its children still lands on the
	/// page itself - a component - so it selects rather than rubber-banding.</summary>
	[Fact]
	public void PressOnActivePageBackground_SelectsThePage()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), OnPageBackground, selectedName: null, pressOriginatedOnAdorner: false);

		Assert.Equal(DesignSurfaceClickAction.SelectComponent, decision.Action);
	}

	/// <summary>With a container selected, a press on its own background (no child there) is a
	/// drag-start for the container, not a drill-through.</summary>
	[Fact]
	public void PressOnAdornerOverTheSelectionsOwnBackground_LetsTheAdornerHandleIt()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), OnPageBackground, selectedName: "tabPage1", pressOriginatedOnAdorner: true);

		Assert.Equal(DesignSurfaceClickAction.LetAdornerHandle, decision.Action);
	}

	// ------------------------------------------------------------------- ancestor walking

	[Fact]
	public void AncestorsOf_WalksTheWholeContainingChainExcludingTheComponentItself()
	{
		var ancestors = DesignSurfaceClickArbiter.AncestorsOf(Fixture(), "button1");

		Assert.Equal(new[] { "tabControl1", "tabPage1", Root }.OrderBy(name => name),
			ancestors.OrderBy(name => name));
		Assert.DoesNotContain("button1", ancestors);
	}

	[Fact]
	public void AncestorsOf_IsEmptyForTheRootAndForAnUnknownOrAbsentSelection()
	{
		Assert.Empty(DesignSurfaceClickArbiter.AncestorsOf(Fixture(), Root));
		Assert.Empty(DesignSurfaceClickArbiter.AncestorsOf(Fixture(), "notAComponent"));
		Assert.Empty(DesignSurfaceClickArbiter.AncestorsOf(Fixture(), null));
		Assert.Empty(DesignSurfaceClickArbiter.AncestorsOf(Fixture(), ""));
	}

	/// <summary>A cyclic Parent chain must not hang the caller - this runs on the UI thread inside a
	/// mouse handler, so a spin here freezes the whole IDE rather than failing visibly.</summary>
	[Fact]
	public void AncestorsOf_TerminatesOnACyclicParentChain()
	{
		var cyclic = new List<DesignSurfaceClickCandidate> {
			new("a", "b", new Rect(0, 0, 10, 10), true),
			new("b", "a", new Rect(0, 0, 10, 10), true)
		};

		var ancestors = DesignSurfaceClickArbiter.AncestorsOf(cyclic, "a");

		Assert.Equal(new[] { "a", "b" }.OrderBy(name => name), ancestors.OrderBy(name => name));
	}

	// ------------------------------------------------------------------------ degenerate input

	[Fact]
	public void NoCandidatesAtAll_StartsAMarqueeRatherThanThrowing()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			new List<DesignSurfaceClickCandidate>(), OnButton,
			selectedName: "gone", pressOriginatedOnAdorner: false);

		Assert.Equal(DesignSurfaceClickAction.StartMarquee, decision.Action);
	}

	/// <summary>A selection that is no longer in the component list (deleted, or a stale name from
	/// the previous document) has no ancestors, so arbitration falls back to plain
	/// "is anything under the pointer" instead of misbehaving.</summary>
	[Fact]
	public void StaleSelectionName_StillArbitratesByWhatIsActuallyUnderThePointer()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), OnButton, selectedName: "deletedButton", pressOriginatedOnAdorner: true);

		Assert.Equal(DesignSurfaceClickAction.SelectComponent, decision.Action);
		Assert.True(decision.ReleaseAdornerCapture);
	}

	/// <summary>Name comparison is ordinal: two components differing only in case are different
	/// components, so a case-variant selection name must not be mistaken for the selection.</summary>
	[Fact]
	public void SelectionMatchingIsOrdinal_NotCaseInsensitive()
	{
		var decision = DesignSurfaceClickArbiter.Decide(
			Fixture(), OnButton, selectedName: "BUTTON1", pressOriginatedOnAdorner: true);

		Assert.Equal(DesignSurfaceClickAction.SelectComponent, decision.Action);
	}

	/// <summary>Without an adorner under the press, the drill-through question is moot - the press
	/// simply belongs to whatever is under it, and nothing needs its capture released.</summary>
	[Fact]
	public void PressNotOnAnAdorner_NeverReportsCaptureToRelease()
	{
		foreach (var point in new[] { OnButton, OnPageBackground, OnEmptyForm }) {
			var decision = DesignSurfaceClickArbiter.Decide(
				Fixture(), point, selectedName: "tabControl1", pressOriginatedOnAdorner: false);
			Assert.NotEqual(DesignSurfaceClickAction.LetAdornerHandle, decision.Action);
		}
	}
}
