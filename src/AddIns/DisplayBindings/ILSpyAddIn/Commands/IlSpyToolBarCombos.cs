// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// The dropdown half of real ILSpy's toolbar. ILSpy hardcodes these directly in
// ILSpy/Controls/MainToolBar.xaml rather than exporting them as commands (only the icon buttons come
// from [ExportToolbarCommand]), so unlike IlSpyToolBarButtons.cs there is no command object to
// mirror - what is mirrored is the XAML's bindings:
//
//   assemblyListComboBox   ItemsSource = AssemblyListManager.AssemblyLists
//                          SelectedItem = SessionSettings.ActiveAssemblyList
//   (button)               ManageAssemblyListsCommand, Owner = the containing Window
//   languageComboBox       ItemsSource = LanguageService.AllLanguages, DisplayMemberPath = Name
//                          SelectedItem = LanguageService.Language
//   languageVersionComboBox ItemsSource = <language>.LanguageVersions, DisplayMemberPath = DisplayName
//                          SelectedItem = LanguageService.LanguageVersion
//                          Visibility bound to <language>.HasLanguageVersions
//
// Switching any of them needs no explicit refresh - the hosted AssemblyTreeModel already reacts:
// SessionSettings.ActiveAssemblyList -> ShowAssemblyList(...), and LanguageSettings' LanguageId /
// LanguageVersionId -> RefreshDecompiledView() (see AssemblyTreeModel's settings handler).
//
// Binding is deferred to the first UpdateStatus() that sees IlSpyWorkspaceHost.IsInitialized, never
// done in Initialize(): the toolbar is built during workbench startup, long before any ILSpy action,
// and every IlSpyWorkspaceHost member except IsInitialized boots the whole hosted ILSpy as a side
// effect. A toolbar being constructed must not be what starts ILSpy.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpyX;

// ComboBox inherits FrameworkElement.Language (an XmlLanguage), which shadows ILSpy's own Language
// type inside these classes - alias both dropdown item types so there is no ambiguity either way.
using IlSpyLanguage = ICSharpCode.ILSpy.Language;
using IlSpyLanguageVersion = ICSharpCode.ILSpyX.LanguageVersion;

namespace ICSharpCode.ILSpyAddIn.Commands
{
	/// <summary>
	/// Shared plumbing for the toolbar dropdowns: toolbar combo chrome, a fixed width (a toolbar
	/// combo has no natural width to grow into), and the deferred one-time bind described in this
	/// file's header. <see cref="Bind"/> runs once, when the hosted ILSpy first exists.
	/// </summary>
	public abstract class IlSpyToolBarComboBoxBase : ComboBox, ICustomToolBarItem, IStatusUpdate
	{
		bool bound;

		protected abstract double ComboWidth { get; }
		protected abstract string ToolTipText { get; }

		/// <summary>Populate items/selection and subscribe to whatever keeps them in sync.</summary>
		protected abstract void Bind();

		/// <summary>Re-read the current selection from ILSpy (after something else changed it).</summary>
		protected abstract void SyncSelection();

		/// <summary>Guards programmatic SelectedItem writes from being taken for user input.</summary>
		protected bool IsSyncing { get; private set; }

		protected void WithoutRaisingUserSelection(Action action)
		{
			bool previous = IsSyncing;
			IsSyncing = true;
			try {
				action();
			} finally {
				IsSyncing = previous;
			}
		}

		public void Initialize(UIElement inputBindingOwner, Codon codon, object caller)
		{
			Width = ComboWidth;
			ToolTip = ToolTipText;
			// ToolBarService's "Custom" branch already applies ToolBar.ComboBoxStyleKey to a ComboBox
			// it creates, but set it here too so the control looks right regardless of how it is
			// hosted (and so this doesn't silently depend on that branch keeping the special case).
			SetResourceReference(FrameworkElement.StyleProperty, ToolBar.ComboBoxStyleKey);
			UpdateStatus();
		}

		public void UpdateStatus()
		{
			if (!IlSpyWorkspaceHost.IsInitialized) {
				IsEnabled = false;
				return;
			}
			IsEnabled = true;
			if (!bound) {
				bound = true;
				try {
					Bind();
				} catch (Exception ex) {
					// A broken toolbar dropdown must not take the workbench's status pass down with it.
					LoggingService.Warn("Could not bind the ILSpy toolbar dropdown " + GetType().Name + ".", ex);
					IsEnabled = false;
					return;
				}
			}
			SyncSelection();
		}

		public void UpdateText()
		{
		}
	}

	/// <summary>
	/// ILSpy's assembly-list dropdown. Items are list *names*
	/// (<see cref="AssemblyListManager.AssemblyLists"/> is an ObservableCollection&lt;string&gt;),
	/// and selecting one writes <c>SessionSettings.ActiveAssemblyList</c>, which AssemblyTreeModel
	/// turns into <c>ShowAssemblyList(...)</c>.
	/// </summary>
	public sealed class IlSpyAssemblyListComboBox : IlSpyToolBarComboBoxBase
	{
		protected override double ComboWidth => 150;
		protected override string ToolTipText => "Select assembly list";

		protected override void Bind()
		{
			var settingsService = IlSpyWorkspaceHost.SettingsService;
			// ObservableCollection, so the dropdown follows lists being created/deleted in the
			// Manage Assembly Lists dialog with no extra wiring.
			ItemsSource = settingsService.AssemblyListManager.AssemblyLists;
			SelectionChanged += OnSelectionChanged;
		}

		protected override void SyncSelection()
		{
			// Prefer the name of the list actually loaded in the tree over
			// SessionSettings.ActiveAssemblyList: the latter is only written when settings are saved
			// (AssemblyTreeModel does `settings.ActiveAssemblyList = AssemblyList.ListName` on save),
			// so on a fresh profile it is still null while "(Default)" is already the loaded list -
			// measured, that left the dropdown showing no selection at all.
			string active = IlSpyWorkspaceHost.AssemblyTreeModel.AssemblyList?.ListName
				?? IlSpyWorkspaceHost.SettingsService.SessionSettings.ActiveAssemblyList;
			if (!Equals(SelectedItem, active))
				WithoutRaisingUserSelection(() => SelectedItem = active);
		}

		void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (IsSyncing || SelectedItem is not string listName)
				return;
			var settingsService = IlSpyWorkspaceHost.SettingsService;
			if (settingsService.SessionSettings.ActiveAssemblyList != listName)
				settingsService.SessionSettings.ActiveAssemblyList = listName;
		}
	}

	/// <summary>Opens ILSpy's own Manage Assembly Lists dialog (Views/ManageAssemblyListsDialog).</summary>
	public sealed class IlSpyManageAssemblyListsToolBarButton : IlSpyToolBarButtonBase
	{
		protected override string IconName => "Library_16x";
		protected override string ToolTipText => "Manage assembly lists";

		protected override void Execute()
		{
			// What ILSpy's ManageAssemblyListsCommand does, minus its SimpleCommand/MEF wrapper: the
			// dialog is linked source in this addin (see ILSpyAddIn.csproj's Views/** items plus the
			// ManageAssemblyListsDialog.xaml Page), so it can simply be constructed.
			var dialog = new ICSharpCode.ILSpy.ManageAssemblyListsDialog(IlSpyWorkspaceHost.SettingsService) {
				Owner = System.Windows.Application.Current?.MainWindow
			};
			dialog.ShowDialog();
		}
	}

	/// <summary>
	/// ILSpy's language dropdown (C# / IL / ...). Writing <see cref="LanguageService.Language"/> is
	/// what makes AssemblyTreeModel re-decompile the current view.
	/// </summary>
	public sealed class IlSpyLanguageComboBox : IlSpyToolBarComboBoxBase
	{
		protected override double ComboWidth => 110;
		protected override string ToolTipText => "Select language";

		protected override void Bind()
		{
			var languageService = IlSpyWorkspaceHost.LanguageService;
			DisplayMemberPath = nameof(IlSpyLanguage.Name);
			ItemsSource = languageService.AllLanguages;
			SelectionChanged += OnSelectionChanged;
			// Keep in step when something other than this dropdown changes the language.
			languageService.PropertyChanged += (_, e) => {
				if (e.PropertyName == nameof(LanguageService.Language))
					SyncSelection();
			};
		}

		protected override void SyncSelection()
		{
			var current = IlSpyWorkspaceHost.LanguageService.Language;
			if (!ReferenceEquals(SelectedItem, current))
				WithoutRaisingUserSelection(() => SelectedItem = current);
		}

		void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (IsSyncing || SelectedItem is not IlSpyLanguage language)
				return;
			var languageService = IlSpyWorkspaceHost.LanguageService;
			if (!ReferenceEquals(languageService.Language, language))
				languageService.Language = language;
		}
	}

	/// <summary>
	/// ILSpy's language-version dropdown. Collapsed for languages that have no versions (IL), which
	/// is what ILSpy's own <c>Visibility</c> binding on <c>HasLanguageVersions</c> does; its item list
	/// belongs to the *currently selected language*, so it is re-read on every status pass rather
	/// than bound once.
	/// </summary>
	public sealed class IlSpyLanguageVersionComboBox : IlSpyToolBarComboBoxBase
	{
		IlSpyLanguage boundLanguage;

		protected override double ComboWidth => 130;
		protected override string ToolTipText => "Select language version";

		protected override void Bind()
		{
			DisplayMemberPath = nameof(IlSpyLanguageVersion.DisplayName);
			SelectionChanged += OnSelectionChanged;
			// React to the language changing instead of waiting for the workbench's next status pass:
			// switching to a language with no versions (IL) has to collapse this dropdown right away,
			// and measured, relying on the periodic pass left it still showing the previous language's
			// version. ILSpy's own XAML is push-based for the same reason.
			IlSpyWorkspaceHost.LanguageService.PropertyChanged += OnLanguageServicePropertyChanged;
		}

		void OnLanguageServicePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(LanguageService.Language) or nameof(LanguageService.LanguageVersion))
				SyncSelection();
		}

		protected override void SyncSelection()
		{
			var languageService = IlSpyWorkspaceHost.LanguageService;
			var language = languageService.Language;

			// Follow the selected language: swap the item list when it changes, and hide entirely for
			// a language with no versions (ILSpy binds Visibility to HasLanguageVersions).
			if (!ReferenceEquals(boundLanguage, language)) {
				boundLanguage = language;
				WithoutRaisingUserSelection(() => ItemsSource = language?.LanguageVersions);
			}
			Visibility = language != null && language.HasLanguageVersions
				? Visibility.Visible
				: Visibility.Collapsed;

			var current = languageService.LanguageVersion;
			if (!ReferenceEquals(SelectedItem, current))
				WithoutRaisingUserSelection(() => SelectedItem = current);
		}

		void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (IsSyncing || SelectedItem is not IlSpyLanguageVersion version)
				return;
			var languageService = IlSpyWorkspaceHost.LanguageService;
			if (!ReferenceEquals(languageService.LanguageVersion, version))
				languageService.LanguageVersion = version;
		}
	}
}
