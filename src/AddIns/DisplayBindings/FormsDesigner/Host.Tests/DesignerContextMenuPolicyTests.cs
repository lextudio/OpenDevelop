using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests
{
	/// <summary>
	/// Covers which designer context menu a right-click opens and what the selection permits. Unit
	/// tests are the only option: a WPF ContextMenu is its own top-level window, invisible to both of
	/// DevFlow's observation channels, so the opened menu cannot be asserted end-to-end at all.
	/// </summary>
	public class DesignerContextMenuPolicyTests
	{
		[Fact]
		public void AControlOnTheSurfaceGetsTheSelectionMenu()
		{
			Assert.Equal(DesignerContextMenuTarget.SurfaceComponent,
				DesignerContextMenuPolicy.TargetFor(tray: false, "button1", "tabPage1"));
		}

		/// <summary>The root form has no z-order, Cut/Copy or Delete of its own, so it gets the
		/// container menu rather than the selection one.</summary>
		[Fact]
		public void TheRootComponentGetsTheContainerMenu()
		{
			Assert.Equal(DesignerContextMenuTarget.SurfaceRoot,
				DesignerContextMenuPolicy.TargetFor(tray: false, "MainForm", ""));
		}

		[Theory]
		[InlineData("")]
		[InlineData(null)]
		public void BareCanvasGetsTheContainerMenuToo(string? componentName)
		{
			// Clicking empty canvas is how the user reaches the form's own commands, so an
			// unresolved press must not come back with no menu at all.
			Assert.Equal(DesignerContextMenuTarget.SurfaceRoot,
				DesignerContextMenuPolicy.TargetFor(tray: false, componentName, null));
		}

		[Fact]
		public void ATrayEntryGetsTheTraySelectionMenu()
		{
			// A tray component has no parent - it is not on the form - and that must not be mistaken
			// for the design root, which is what the parentName argument means on the surface.
			Assert.Equal(DesignerContextMenuTarget.TrayComponent,
				DesignerContextMenuPolicy.TargetFor(tray: true, "timer1", ""));
		}

		[Theory]
		[InlineData("")]
		[InlineData(null)]
		public void TheTrayBackgroundIsItsOwnTarget(string? componentName)
		{
			// The tray background is a real target with nothing under the cursor: it is where
			// Paste-a-component belongs.
			Assert.Equal(DesignerContextMenuTarget.TrayBackground,
				DesignerContextMenuPolicy.TargetFor(tray: true, componentName, null));
		}

		[Fact]
		public void AControlWithAContainerIsRemovable()
		{
			Assert.True(DesignerContextMenuPolicy.IsRemovable(isTrayComponent: false, "tabPage1"));
		}

		[Theory]
		[InlineData("")]
		[InlineData(null)]
		public void TheDesignRootIsNotRemovable(string? parentName)
		{
			// The form owns the surface; Cut/Delete on it is meaningless.
			Assert.False(DesignerContextMenuPolicy.IsRemovable(isTrayComponent: false, parentName));
		}

		/// <summary>The regression this rule exists for. Writing it as "has a parent" - which reads
		/// identically for controls - disabled Cut/Copy/Delete on every tray component, and in the
		/// selection helper it silently dropped them so Delete reported success and did nothing.
		/// A Timer, an ImageList and a ToolTip all have no parent control because they are not on
		/// the form at all.</summary>
		[Theory]
		[InlineData("")]
		[InlineData(null)]
		public void ATrayComponentIsRemovableDespiteHavingNoParent(string? parentName)
		{
			Assert.True(DesignerContextMenuPolicy.IsRemovable(isTrayComponent: true, parentName));
		}
	}
}
