// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// Dedicated ILSpy toolbar buttons, so the hosted ILSpy strip carries more than just
// "Open Assembly..." and lines up with real ILSpy's own toolbar. Real ILSpy builds its toolbar from
// [ExportToolbarCommand] attributes (ILSpy/Controls/MainToolBar.xaml composes the categories); the
// commands behind the buttons mirrored here are:
//
//   Navigation  Back            Commands/BrowseBackCommand.cs      -> AssemblyTreeModel.NavigateHistory(false)
//   Navigation  Forward         Commands/BrowseForwardCommand.cs   -> AssemblyTreeModel.NavigateHistory(true)
//   Open        Open            Commands/OpenCommand.cs            -> (OpenAssemblyCommand.cs here)
//   Open        Reload          Commands/RefreshCommand.cs         -> AssemblyTreeModel.Refresh()
//   View        Search          Search/ShowSearchCommand.cs        -> show/focus the Search pane
//   View        Sort            Commands/SortAssemblyListCommand.cs-> AssemblyTreeModel.SortAssemblyList()
//   View        Collapse nodes  Commands/SortAssemblyListCommand.cs-> AssemblyTreeModel.CollapseAll()
//
// Every one of those ILSpy commands does nothing but delegate to an AssemblyTreeModel method, so
// these call the same model methods directly rather than resolving ILSpy's own MEF command objects
// (which are `internal sealed` and bound to ILSpy's composition/DockWorkspace anyway). Same
// behavior, no dependency on ILSpy's command plumbing.
//
// These are AddInTree type="Custom" items rather than ordinary type="Item" ones with an `icon=`
// attribute, because `icon=` resolves through PresentationResourceService.GetBitmapSource - i.e.
// the shell's *bitmap* resource bundle (data/resources/image/BitmapResources), which knows nothing
// about the VS2017 Image Library XAML vector icons this addin embeds under Icons/. Going through
// ICustomToolBarItem lets each button supply its own ImageSource via VsIconLoader, keeping the
// icons vector and the whole thing self-contained in this addin (no shell change needed).
using System;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;

namespace ICSharpCode.ILSpyAddIn.Commands
{
	/// <summary>
	/// Base for the hosted-ILSpy toolbar buttons: renders one of this addin's embedded VS2017 Image
	/// Library vector icons and runs <see cref="Execute"/> on click. <see cref="CanExecute"/> drives
	/// the button's enabled state, refreshed by the shell's own
	/// <see cref="ToolBarService.UpdateStatus(System.Collections.IEnumerable)"/> pass via
	/// <see cref="IStatusUpdate"/>.
	/// </summary>
	public abstract class IlSpyToolBarButtonBase : Button, ICustomToolBarItem, IStatusUpdate
	{
		/// <summary>Name of the embedded icon, without the "Icons." prefix or ".xaml" suffix.</summary>
		protected abstract string IconName { get; }

		/// <summary>Tooltip text; mirrors the wording of the corresponding real ILSpy command.</summary>
		protected abstract string ToolTipText { get; }

		protected abstract void Execute();

		/// <summary>Defaults to "enabled once the hosted ILSpy has actually been initialized".</summary>
		protected virtual bool CanExecute()
		{
			return IlSpyWorkspaceHost.IsInitialized;
		}

		public void Initialize(UIElement inputBindingOwner, Codon codon, object caller)
		{
			// Match the shell's own toolbar buttons: 16px image, shared toolbar image style (which is
			// what dims the icon when the button is disabled).
			var image = new Image {
				Source = VsIconLoader.Load(IconName),
				Height = 16,
				Width = 16
			};
			image.SetResourceReference(FrameworkElement.StyleProperty, ToolBarService.ImageStyleKey);
			Content = image;
			ToolTip = ToolTipText;
			Click += (_, _) => {
				try {
					Execute();
				} catch (Exception ex) {
					MessageService.ShowException(ex);
				}
			};
			UpdateStatus();
		}

		public void UpdateStatus()
		{
			// Never call CanExecute before the addin exists - every IlSpyWorkspaceHost member except
			// IsInitialized initializes the whole hosted ILSpy as a side effect, and a toolbar status
			// refresh must not be what boots it.
			IsEnabled = IlSpyWorkspaceHost.IsInitialized && CanExecute();
		}

		public void UpdateText()
		{
		}
	}

	/// <summary>Mirrors ILSpy's Navigation/Back toolbar command (BrowseBackCommand.cs).</summary>
	public sealed class IlSpyBrowseBackToolBarButton : IlSpyToolBarButtonBase
	{
		protected override string IconName => "Backward_16x";
		protected override string ToolTipText => "Back";
		protected override bool CanExecute() => IlSpyWorkspaceHost.AssemblyTreeModel.CanNavigateBack;
		protected override void Execute() => IlSpyWorkspaceHost.AssemblyTreeModel.NavigateHistory(forward: false);
	}

	/// <summary>Mirrors ILSpy's Navigation/Forward toolbar command (BrowseForwardCommand.cs).</summary>
	public sealed class IlSpyBrowseForwardToolBarButton : IlSpyToolBarButtonBase
	{
		protected override string IconName => "Forward_16x";
		protected override string ToolTipText => "Forward";
		protected override bool CanExecute() => IlSpyWorkspaceHost.AssemblyTreeModel.CanNavigateForward;
		protected override void Execute() => IlSpyWorkspaceHost.AssemblyTreeModel.NavigateHistory(forward: true);
	}

	/// <summary>Mirrors ILSpy's Open/Reload toolbar command (RefreshCommand.cs).</summary>
	public sealed class IlSpyRefreshToolBarButton : IlSpyToolBarButtonBase
	{
		protected override string IconName => "Refresh_16x";
		protected override string ToolTipText => "Reload all assemblies";
		protected override void Execute() => IlSpyWorkspaceHost.AssemblyTreeModel.Refresh();
	}

	/// <summary>Mirrors ILSpy's View/Sort toolbar command (SortAssemblyListCommand.cs).</summary>
	public sealed class IlSpySortAssemblyListToolBarButton : IlSpyToolBarButtonBase
	{
		protected override string IconName => "SortAscending_16x";
		protected override string ToolTipText => "Sort assembly list by name";
		protected override void Execute() => IlSpyWorkspaceHost.AssemblyTreeModel.SortAssemblyList();
	}

	/// <summary>Mirrors ILSpy's View/Collapse toolbar command (CollapseAllCommand in SortAssemblyListCommand.cs).</summary>
	public sealed class IlSpyCollapseAllToolBarButton : IlSpyToolBarButtonBase
	{
		protected override string IconName => "CollapseAll_16x";
		protected override string ToolTipText => "Collapse tree nodes";
		protected override void Execute() => IlSpyWorkspaceHost.AssemblyTreeModel.CollapseAll();
	}

	/// <summary>
	/// Mirrors ILSpy's View/Search toolbar command (Search/ShowSearchCommand.cs). Uses the
	/// non-destructive activation path - see IlSpyWorkspaceHost.ActivatePane's own note on why
	/// re-registering an anchorable (od.ilspy.show-pane) must not be used for this.
	/// </summary>
	public sealed class IlSpyShowSearchToolBarButton : IlSpyToolBarButtonBase
	{
		protected override string IconName => "Search";
		protected override string ToolTipText => "Search assemblies";
		protected override void Execute() => IlSpyWorkspaceHost.ActivatePane("Search");
	}

	/// <summary>
	/// Base for the three API-visibility toggles, mirroring the CheckBoxes real ILSpy hardcodes in
	/// ILSpy/Controls/MainToolBar.xaml (bound to SessionSettings.LanguageSettings.ApiVisPublicOnly /
	/// ApiVisPublicAndInternal / ApiVisAll). Those three bools are a radio group over one enum,
	/// <c>LanguageSettings.ShowApiLevel</c> (<c>ApiVisibility</c>: PublicOnly / PublicAndInternal /
	/// All) - setting any of them switches the enum - so these are mutually exclusive by
	/// construction, and each one re-reads the enum to decide whether it is the checked one.
	///
	/// Changing the level needs no explicit refresh: AssemblyTreeModel already subscribes to
	/// LanguageSettings' PropertyChanged and calls Refresh() for any property other than
	/// LanguageId/LanguageVersionId (AssemblyTreeModel.cs's settings handler), which re-filters the
	/// assembly tree.
	///
	/// A CheckBox rather than a Button because that is what ILSpy uses and what conveys a sticky
	/// on/off state; styled with ToolBar.CheckBoxStyleKey exactly as the shell's own
	/// ToolBarCheckBox does, so it gets flat toolbar chrome instead of a stock check box.
	/// </summary>
	public abstract class IlSpyApiVisibilityToggleBase : CheckBox, ICustomToolBarItem, IStatusUpdate
	{
		protected abstract string IconName { get; }
		protected abstract string ToolTipText { get; }

		/// <summary>The level this toggle selects when checked.</summary>
		protected abstract ICSharpCode.ILSpyX.ApiVisibility Level { get; }

		public void Initialize(UIElement inputBindingOwner, Codon codon, object caller)
		{
			var image = new Image {
				Source = VsIconLoader.Load(IconName),
				Height = 16,
				Width = 16
			};
			image.SetResourceReference(FrameworkElement.StyleProperty, ToolBarService.ImageStyleKey);
			Content = image;
			ToolTip = ToolTipText;
			SetResourceReference(FrameworkElement.StyleProperty, ToolBar.CheckBoxStyleKey);
			Checked += OnChecked;
			UpdateStatus();
		}

		void OnChecked(object sender, RoutedEventArgs e)
		{
			if (!IlSpyWorkspaceHost.IsInitialized)
				return;
			IlSpyWorkspaceHost.SetApiVisibility(Level);
			// The other two toggles read the same enum, so refresh the whole strip's state rather
			// than only this one.
			IlSpyApiVisibilityToggles.UpdateAll();
		}

		public void UpdateStatus()
		{
			// Must not touch any other IlSpyWorkspaceHost member first: everything except
			// IsInitialized boots the whole hosted ILSpy as a side effect, and a toolbar status
			// refresh must not be what does that.
			if (!IlSpyWorkspaceHost.IsInitialized) {
				IsEnabled = false;
				return;
			}
			IsEnabled = true;
			bool isCurrent = IlSpyWorkspaceHost.GetApiVisibility() == Level;
			if (IsChecked != isCurrent) {
				// Assign without re-entering OnChecked (which would be a no-op set anyway, but this
				// keeps the intent explicit).
				Checked -= OnChecked;
				IsChecked = isCurrent;
				Checked += OnChecked;
			}
		}

		public void UpdateText()
		{
		}
	}

	/// <summary>Tracks the live API-visibility toggles so selecting one can refresh the others.</summary>
	static class IlSpyApiVisibilityToggles
	{
		static readonly System.Collections.Generic.List<System.WeakReference<IlSpyApiVisibilityToggleBase>> toggles = new();

		public static void Register(IlSpyApiVisibilityToggleBase toggle)
		{
			toggles.Add(new System.WeakReference<IlSpyApiVisibilityToggleBase>(toggle));
		}

		public static void UpdateAll()
		{
			foreach (var reference in toggles.ToArray()) {
				if (reference.TryGetTarget(out var toggle))
					toggle.UpdateStatus();
				else
					toggles.Remove(reference);
			}
		}
	}

	/// <summary>Show public types and members only (ILSpy: ShowPublicOnlyTypesMembers).</summary>
	public sealed class IlSpyShowPublicOnlyToggle : IlSpyApiVisibilityToggleBase
	{
		public IlSpyShowPublicOnlyToggle() => IlSpyApiVisibilityToggles.Register(this);
		// Plain (unadorned) VS2017 member icon - in VS iconography that means public.
		protected override string IconName => "Method_16x";
		protected override string ToolTipText => "Show public types and members";
		protected override ICSharpCode.ILSpyX.ApiVisibility Level => ICSharpCode.ILSpyX.ApiVisibility.PublicOnly;
	}

	/// <summary>Show public and internal types and members (ILSpy: ShowInternalTypesMembers).</summary>
	public sealed class IlSpyShowPublicAndInternalToggle : IlSpyApiVisibilityToggleBase
	{
		public IlSpyShowPublicAndInternalToggle() => IlSpyApiVisibilityToggles.Register(this);
		// "Friend" is VS iconography's name for internal - the lowest visibility this level includes.
		protected override string IconName => "MethodFriend_16x";
		protected override string ToolTipText => "Show public and internal types and members";
		protected override ICSharpCode.ILSpyX.ApiVisibility Level => ICSharpCode.ILSpyX.ApiVisibility.PublicAndInternal;
	}

	/// <summary>Show all types and members, private included (ILSpy: ShowAllTypesAndMembers).</summary>
	public sealed class IlSpyShowAllToggle : IlSpyApiVisibilityToggleBase
	{
		public IlSpyShowAllToggle() => IlSpyApiVisibilityToggles.Register(this);
		// Private is the lowest visibility this level includes.
		protected override string IconName => "MethodPrivate_16x";
		protected override string ToolTipText => "Show all types and members";
		protected override ICSharpCode.ILSpyX.ApiVisibility Level => ICSharpCode.ILSpyX.ApiVisibility.All;
	}
}
