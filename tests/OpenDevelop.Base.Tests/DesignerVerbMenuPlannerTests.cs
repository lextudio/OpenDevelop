using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests
{
	/// <summary>
	/// Covers what a design-surface context menu offers. These have to be unit tests: a WPF
	/// ContextMenu is its own top-level window, so it is invisible to BOTH of DevFlow's observation
	/// channels (screenshot and ui/tree) and its content cannot be asserted end-to-end at all.
	/// </summary>
	public class DesignerVerbMenuPlannerTests
	{
		/// <summary>The fixture the live TabControl work used: a form holding a TabControl whose
		/// pages each hold a button.</summary>
		static readonly Dictionary<string, string> TabFixtureParents = new Dictionary<string, string>(StringComparer.Ordinal) {
			{ "MainForm", "" },
			{ "tabControl1", "MainForm" },
			{ "tabPage1", "tabControl1" },
			{ "tabPage2", "tabControl1" },
			{ "button1", "tabPage1" },
		};

		static Func<string, string?> ParentsOf(IReadOnlyDictionary<string, string> parents)
			=> name => parents.TryGetValue(name, out var parent) ? parent : null;

		/// <summary>The reason the walk exists at all: right-clicking a tab PAGE has to reach the
		/// TabControl, because Add Tab/Remove Tab live on the TabControl's designer while the page's
		/// own designer publishes nothing.</summary>
		[Fact]
		public void AClickedTabPageReachesItsTabControl()
		{
			Assert.Equal(
				new[] { "tabPage1", "tabControl1" },
				DesignerVerbMenuPlanner.ComponentAndItsContainers("tabPage1", ParentsOf(TabFixtureParents)));
		}

		/// <summary>The walk stops at the immediate container rather than running to the root.
		/// Verified against the live designer: with the full chain, right-clicking button1 offered
		/// "Add Tab" - inherited from the TabControl two levels up - which real VS does not do.</summary>
		[Fact]
		public void TheWalkStopsAtTheImmediateContainerSoDistantContainersDoNotLeakIn()
		{
			var chain = DesignerVerbMenuPlanner.ComponentAndItsContainers("button1", ParentsOf(TabFixtureParents));
			Assert.Equal(new[] { "button1", "tabPage1" }, chain);
			Assert.DoesNotContain("tabControl1", chain);
		}

		[Fact]
		public void TheDesignRootIsAChainOfJustItself()
		{
			Assert.Equal(
				new[] { "MainForm" },
				DesignerVerbMenuPlanner.ComponentAndItsContainers("MainForm", ParentsOf(TabFixtureParents)));
		}

		[Fact]
		public void AComponentMissingFromTheTreeIsStillItsOwnChain()
		{
			// The clicked name comes from a server hit-test and the tree from a separate snapshot,
			// so the two can disagree. The menu must still offer the component's own entries.
			Assert.Equal(
				new[] { "ghost" },
				DesignerVerbMenuPlanner.ComponentAndItsContainers("ghost", ParentsOf(TabFixtureParents)));
		}

		[Theory]
		[InlineData("")]
		[InlineData(null)]
		public void AnEmptyClickTargetYieldsNothing(string componentName)
		{
			Assert.Empty(DesignerVerbMenuPlanner.ComponentAndItsContainers(componentName, ParentsOf(TabFixtureParents)));
		}

		/// <summary>Parent links arrive over the wire from the child designer process, so a cycle
		/// is possible and must not hang the menu build.</summary>
		[Fact]
		public void ACycleInTheParentLinksTerminates()
		{
			var cyclic = new Dictionary<string, string>(StringComparer.Ordinal) {
				{ "a", "b" }, { "b", "c" }, { "c", "a" },
			};
			// Bounded by the visited set independently of the depth limit, so an explicitly deep
			// walk over a cycle still terminates rather than relying on the default depth.
			Assert.Equal(new[] { "a", "b", "c" },
				DesignerVerbMenuPlanner.ComponentAndItsContainers("a", ParentsOf(cyclic), depth: 10));
		}

		[Fact]
		public void AComponentThatIsItsOwnParentTerminates()
		{
			var selfParented = new Dictionary<string, string>(StringComparer.Ordinal) { { "a", "a" } };
			Assert.Equal(new[] { "a" },
				DesignerVerbMenuPlanner.ComponentAndItsContainers("a", ParentsOf(selfParented)));
		}

		[Fact]
		public void PlanKeepsTheGatheredOrderSoTheClickedComponentsVerbsComeFirst()
		{
			var planned = DesignerVerbMenuPlanner.Plan(new[] {
				new DesignerVerbCandidate("tabPage1", "Page Thing", 0),
				new DesignerVerbCandidate("tabControl1", "Add Tab", 0),
				new DesignerVerbCandidate("tabControl1", "Remove Tab", 1),
			});
			Assert.Equal(new[] { "Page Thing", "Add Tab", "Remove Tab" }, planned.Select(entry => entry.Text));
		}

		/// <summary>Each entry has to remember which component published it, because invoking a verb
		/// targets the OWNER rather than what the user right-clicked - and the whole point of the
		/// container walk is that those differ.</summary>
		[Fact]
		public void EachEntryCarriesTheOwnerAndIndexNeededToInvokeIt()
		{
			var planned = DesignerVerbMenuPlanner.Plan(new[] {
				new DesignerVerbCandidate("tabControl1", "Remove Tab", 1),
			});
			var entry = Assert.Single(planned);
			Assert.Equal("tabControl1", entry.OwnerName);
			Assert.Equal(1, entry.Index);
		}

		/// <summary>Nested containers of the same type publish identically-named verbs. Showing both
		/// would give the user two identical menu items that do different things, so the nearest
		/// owner - first in the innermost-first input - wins.</summary>
		[Fact]
		public void TheNearestOwnerWinsADuplicateVerbName()
		{
			var planned = DesignerVerbMenuPlanner.Plan(new[] {
				new DesignerVerbCandidate("innerTabs", "Add Tab", 0),
				new DesignerVerbCandidate("outerTabs", "Add Tab", 0),
			});
			var entry = Assert.Single(planned);
			Assert.Equal("innerTabs", entry.OwnerName);
		}

		[Fact]
		public void AVerbWithNoTextIsDroppedRatherThanBecomingABlankMenuItem()
		{
			var planned = DesignerVerbMenuPlanner.Plan(new[] {
				new DesignerVerbCandidate("tabControl1", "", 0),
				new DesignerVerbCandidate("tabControl1", null, 1),
				new DesignerVerbCandidate("tabControl1", "Add Tab", 2),
			});
			Assert.Equal(new[] { "Add Tab" }, planned.Select(entry => entry.Text));
		}

		/// <summary>A TabPage's own designer publishes no verbs, so before the container walk this
		/// was the state that made the menu offer nothing but Delete.</summary>
		[Fact]
		public void NoVerbsAnywhereInTheChainPlansAnEmptyMenu()
		{
			Assert.Empty(DesignerVerbMenuPlanner.Plan(Array.Empty<DesignerVerbCandidate>()));
		}

		[Fact]
		public void PlanRejectsANullSequenceRatherThanThrowingLaterInsideTheMenuBuild()
		{
			Assert.Throws<ArgumentNullException>(() => DesignerVerbMenuPlanner.Plan(null));
		}

		[Fact]
		public void TheChainRejectsAMissingParentLookup()
		{
			Assert.Throws<ArgumentNullException>(
				() => DesignerVerbMenuPlanner.ComponentAndItsContainers("button1", null).ToList());
		}
	}
}
