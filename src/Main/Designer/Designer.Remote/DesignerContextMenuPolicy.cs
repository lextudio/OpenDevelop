using System;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>Which of a designer's context menus a right-click should open.</summary>
	/// <remarks>
	/// An enum rather than a menu path, so this decision stays testable without the AddIn tree: the
	/// caller maps these onto its own declared paths. The four cases are not interchangeable - the
	/// design root has no z-order or Cut/Copy of its own, and the tray is a separate surface whose
	/// background is a real target even with nothing under the cursor.
	/// </remarks>
	public enum DesignerContextMenuTarget
	{
		/// <summary>A control on the design surface.</summary>
		SurfaceComponent,
		/// <summary>The design surface itself, or the root component that owns it.</summary>
		SurfaceRoot,
		/// <summary>An entry in the component tray.</summary>
		TrayComponent,
		/// <summary>The component tray's own background.</summary>
		TrayBackground,
	}

	/// <summary>Decides what a designer context menu targets and what the selection permits. Pure,
	/// so the rules can be tested without a live designer - which matters here because a WPF
	/// ContextMenu is its own top-level window and is invisible to both of DevFlow's observation
	/// channels, and because both of these rules have already been wrong once in a way no menu
	/// screenshot could have shown.</summary>
	public static class DesignerContextMenuPolicy
	{
		/// <summary>Which menu a right-click opens.</summary>
		/// <param name="tray">Whether the press landed on the component tray rather than the design
		/// surface.</param>
		/// <param name="componentName">The component the press resolved to, empty for background.</param>
		/// <param name="parentName">That component's container, empty when it is the design root.
		/// Ignored for the tray, where a component legitimately has no parent.</param>
		public static DesignerContextMenuTarget TargetFor(bool tray, string? componentName, string? parentName)
		{
			if (tray)
				return String.IsNullOrEmpty(componentName)
					? DesignerContextMenuTarget.TrayBackground
					: DesignerContextMenuTarget.TrayComponent;
			// An unresolved press and the root component get the same menu: clicking bare canvas is
			// how the user reaches the form's own commands.
			return String.IsNullOrEmpty(componentName) || String.IsNullOrEmpty(parentName)
				? DesignerContextMenuTarget.SurfaceRoot
				: DesignerContextMenuTarget.SurfaceComponent;
		}

		/// <summary>Whether the selected component may be cut, copied or deleted - i.e. anything
		/// except the design root, which owns the surface.</summary>
		/// <remarks>
		/// **"Has a parent" is not this test**, though it looks like it, and writing it that way was
		/// a real bug: a component-tray component (a Timer, an ImageList, a ToolTip) has no parent
		/// control because it is not on the form at all. The parent test therefore disabled Cut,
		/// Copy and Delete for every tray component - and, in the selection helper that fed the
		/// delete, silently dropped them so that Delete reported success and did nothing.
		/// </remarks>
		public static bool IsRemovable(bool isTrayComponent, string? parentName)
			=> isTrayComponent || !String.IsNullOrEmpty(parentName);
	}
}
