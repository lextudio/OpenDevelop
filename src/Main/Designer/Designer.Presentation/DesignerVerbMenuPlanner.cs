using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>One verb offered by one component's designer, as the client sees it after a
	/// list-verbs round trip.</summary>
	/// <param name="OwnerName">The component whose designer published the verb. Carried through to
	/// the menu entry because invoking a verb has to target the OWNER, not whatever the user
	/// right-clicked - the whole point of walking the container chain is that these differ.</param>
	/// <param name="Text">The verb's display text, and its identity for de-duplication.</param>
	/// <param name="Index">The verb's index in its owner's own verb collection, which is what
	/// design/invoke-verb takes. Only meaningful together with <paramref name="OwnerName"/>.</param>
	public readonly record struct DesignerVerbCandidate(string OwnerName, string Text, int Index);

	/// <summary>Decides what a design-surface context menu offers. Pure, so the container walk and
	/// the de-duplication can be tested without a live designer or a WPF popup - which matters more
	/// than usual here, because a WPF ContextMenu is its own top-level window and is therefore
	/// invisible to BOTH of DevFlow's observation channels (screenshot and ui/tree). The menu's
	/// content cannot be asserted end-to-end at all; it can only be tested here.</summary>
	public static class DesignerVerbMenuPlanner
	{
		/// <summary>How many components a context menu gathers verbs from: the clicked one and its
		/// immediate container.</summary>
		/// <remarks>
		/// Not the whole ancestor chain, which was tried first and read as noise: right-clicking a
		/// Button inside a TabPage then offered "Add Tab", because the TabControl was further up the
		/// chain. Real VS does not do that. Stopping at the immediate container is what matches it -
		/// a TabPage's menu carries its TabControl's Add Tab/Remove Tab, a Button's does not.
		/// </remarks>
		public const int ContainerDepth = 2;

		/// <summary>Yields <paramref name="componentName"/> then its container, innermost first -
		/// the order verbs must be collected in.</summary>
		/// <remarks>
		/// Verbs are gathered from the container too, not the clicked component alone, because a
		/// container's commands are otherwise unreachable from inside it. TabControl is the case
		/// that forces this: Add Tab / Remove Tab belong to the TabControl's designer, but its pages
		/// cover nearly all of its surface, so right-clicking the page area - the obvious gesture
		/// for "add another tab" - resolves to the TabPage, whose own designer publishes no verbs at
		/// all. Without this the menu offers no way to add a tab.
		///
		/// The walk is bounded by a visited set as well as by <paramref name="depth"/>, because the
		/// parent links come off the wire from the child designer process. A stale or malformed tree
		/// must not spin a context-menu build into an infinite loop.
		/// </remarks>
		public static IEnumerable<string> ComponentAndItsContainers(
			string componentName, Func<string, string?> parentOf, int depth = ContainerDepth)
		{
			if (parentOf == null)
				throw new ArgumentNullException(nameof(parentOf));
			var visited = new HashSet<string>(StringComparer.Ordinal);
			var current = componentName;
			for (var remaining = depth; remaining > 0 && !String.IsNullOrEmpty(current) && visited.Add(current); remaining--) {
				yield return current;
				current = parentOf(current);
			}
		}

		/// <summary>Collapses verbs gathered up the container chain into the menu's final entries,
		/// dropping a duplicate display text.</summary>
		/// <remarks>
		/// The nearest owner wins a duplicate, which is why the input must already be in
		/// innermost-first order: a container's verb must never shadow the clicked component's own
		/// same-named one. Two components in a chain publishing the same verb text is not
		/// hypothetical - nested containers of the same type do it - and showing both would give
		/// the user two identical menu items that do different things.
		/// </remarks>
		public static IReadOnlyList<DesignerVerbCandidate> Plan(
			IEnumerable<DesignerVerbCandidate> verbsInnermostFirst)
		{
			if (verbsInnermostFirst == null)
				throw new ArgumentNullException(nameof(verbsInnermostFirst));
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var planned = new List<DesignerVerbCandidate>();
			foreach (var verb in verbsInnermostFirst) {
				if (String.IsNullOrEmpty(verb.Text) || !seen.Add(verb.Text))
					continue;
				planned.Add(verb);
			}
			return planned;
		}
	}
}
