// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.ViewModels;

namespace ICSharpCode.SharpDevelop.Workbench
{
	sealed class AvalonWorkbenchWindow : PaneModel, IWorkbenchWindow, IOwnerState
	{
		AvalonDockLayout dockLayout;
		ImageSource icon;
		object content;
		object toolTip;

		public AvalonWorkbenchWindow(AvalonDockLayout dockLayout)
		{
			if (dockLayout == null)
				throw new ArgumentNullException("dockLayout");

			this.dockLayout = dockLayout;
			viewContents = new ViewContentCollection(this);

			SD.ResourceService.LanguageChanged += OnTabPageTextChanged;

			PropertyChanged += AvalonWorkbenchWindow_PropertyChanged;
		}

		void AvalonWorkbenchWindow_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(IsActive))
				OnIsActiveChanged();
		}

		void OnIsActiveChanged()
		{
			if (!IsActive)
				return;
			IViewContent vc = ActiveViewContent;
			if (vc != null)
				SetFocus(() => vc.InitiallyFocusedControl as IInputElement);
		}

		internal static void SetFocus(Func<IInputElement> activeChildFunc)
		{
			Application.Current?.Dispatcher.BeginInvoke(
				DispatcherPriority.Loaded,
				new Action(
					delegate {
						IInputElement activeChild = activeChildFunc();
						if (activeChild != null) {
							Keyboard.Focus(activeChild);
						}
					}));
		}

		public bool IsDisposed { get { return false; } }

		public ImageSource Icon {
			get { return icon; }
			set { SetProperty(ref icon, value); }
		}

		public object Content {
			get { return content; }
			private set { SetProperty(ref content, value); }
		}

		public object ToolTip {
			get { return toolTip; }
			private set { SetProperty(ref toolTip, value); }
		}

		#region IOwnerState
		[Flags]
		public enum OpenFileTabStates {
			Nothing             = 0,
			FileDirty           = 1,
			FileReadOnly        = 2,
			FileUntitled        = 4,
			ViewContentWithoutFile = 8
		}

		public System.Enum InternalState {
			get {
				IViewContent content = this.ActiveViewContent;
				OpenFileTabStates state = OpenFileTabStates.Nothing;
				if (content != null) {
					if (content.IsDirty)
						state |= OpenFileTabStates.FileDirty;
					if (content.IsReadOnly)
						state |= OpenFileTabStates.FileReadOnly;
					if (content.PrimaryFile != null && content.PrimaryFile.IsUntitled)
						state |= OpenFileTabStates.FileUntitled;
					if (content.PrimaryFile == null)
						state |= OpenFileTabStates.ViewContentWithoutFile;
				}
				return state;
			}
		}
		#endregion

		TabControl viewTabControl;

		/// <summary>
		/// The current view content which is shown inside this window.
		/// </summary>
		public IViewContent ActiveViewContent {
			get {
				SD.MainThread.VerifyAccess();
				if (viewTabControl != null && viewTabControl.SelectedIndex >= 0 && viewTabControl.SelectedIndex < ViewContents.Count) {
					return ViewContents[viewTabControl.SelectedIndex];
				} else if (ViewContents.Count == 1) {
					return ViewContents[0];
				} else {
					return null;
				}
			}
			set {
				int pos = ViewContents.IndexOf(value);
				if (pos < 0)
					throw new ArgumentException();
				SwitchView(pos);
			}
		}

		public event EventHandler ActiveViewContentChanged;

		IViewContent oldActiveViewContent;

		void UpdateActiveViewContent()
		{
			UpdateTitleAndInfoTip();

			IViewContent newActiveViewContent = this.ActiveViewContent;

			if (oldActiveViewContent != newActiveViewContent && ActiveViewContentChanged != null) {
				ActiveViewContentChanged(this, EventArgs.Empty);
			}
			oldActiveViewContent = newActiveViewContent;
			CommandManager.InvalidateRequerySuggested();
		}

		sealed class ViewContentCollection : Collection<IViewContent>
		{
			readonly AvalonWorkbenchWindow window;

			internal ViewContentCollection(AvalonWorkbenchWindow window)
			{
				this.window = window;
			}

			protected override void ClearItems()
			{
				foreach (IViewContent vc in this) {
					window.UnregisterContent(vc);
				}

				base.ClearItems();
				window.ClearContent();
				window.UpdateActiveViewContent();
			}

			protected override void InsertItem(int index, IViewContent item)
			{
				base.InsertItem(index, item);

				window.RegisterNewContent(item);

				if (Count == 1) {
					window.Content = item.Control;
				} else {
					if (Count == 2) {
						window.CreateViewTabControl();
						IViewContent oldItem = this[0];
						if (oldItem == item) oldItem = this[1];

						TabItem oldPage = new TabItem();
						oldPage.Header = StringParser.Parse(oldItem.TabPageText);
						oldPage.Content = oldItem.Control;
						window.viewTabControl.Items.Add(oldPage);
					}

				TabItem newPage = new TabItem();
				newPage.Header = StringParser.Parse(item.TabPageText);
				newPage.Content = item.Control;

					window.viewTabControl.Items.Insert(index, newPage);
				}
				window.UpdateActiveViewContent();
			}

			protected override void RemoveItem(int index)
			{
				window.UnregisterContent(this[index]);

				base.RemoveItem(index);

				if (Count < 2) {
					window.ClearContent();
					if (Count == 1) {
						window.Content = this[0].Control;
					}
				} else {
					window.viewTabControl.Items.RemoveAt(index);
				}
				window.UpdateActiveViewContent();
			}

			protected override void SetItem(int index, IViewContent item)
			{
				window.UnregisterContent(this[index]);

				base.SetItem(index, item);

				window.RegisterNewContent(item);

				if (Count == 1) {
					window.ClearContent();
					window.Content = item.Control;
				} else {
					TabItem page = (TabItem)window.viewTabControl.Items[index];
					page.Content = item.Control;
					page.Header = StringParser.Parse(item.TabPageText);
				}
				window.UpdateActiveViewContent();
			}
		}

		readonly ViewContentCollection viewContents;

		public IList<IViewContent> ViewContents {
			get { return viewContents; }
		}

		/// <summary>
		/// Gets whether any contained view content has changed
		/// since the last save/load operation.
		/// </summary>
		public bool IsDirty {
			get { return this.ViewContents.Any(vc => vc.IsDirty); }
		}

		public void SwitchView(int viewNumber)
		{
			if (viewTabControl != null) {
				this.viewTabControl.SelectedIndex = viewNumber;

				IViewContent vc = this.ActiveViewContent;
				if (vc != null && this.IsActive)
					SetFocus(() => vc.InitiallyFocusedControl as IInputElement);
			}
		}

		public void SelectWindow()
		{
			this.IsActive = true;
		}

		void Dispose()
		{
			SD.ResourceService.LanguageChanged -= OnTabPageTextChanged;
			// DetachContent must be called before the controls are disposed
			List<IViewContent> viewContents = this.ViewContents.ToList();
			this.ViewContents.Clear();
			viewContents.ForEach(vc => vc.Dispose());
		}

		sealed class TabControlWithModifiedShortcuts : TabControl
		{
			readonly AvalonWorkbenchWindow parentWindow;

			public TabControlWithModifiedShortcuts(AvalonWorkbenchWindow parentWindow)
			{
				this.parentWindow = parentWindow;
			}

			protected override void OnKeyDown(KeyEventArgs e)
			{
				// We don't call base.KeyDown to prevent the TabControl from handling Ctrl+Tab.
				// Instead, we let the key press bubble up to the DocumentPane.
			}

			protected override void OnPreviewKeyDown(KeyEventArgs e)
			{
				base.OnPreviewKeyDown(e);
				if (e.Handled)
					return;

				// However, we do want to handle Ctrl+PgUp / Ctrl+PgDown (SD-1735)
				if ((e.Key == Key.PageUp || e.Key == Key.PageDown) && e.KeyboardDevice.Modifiers == ModifierKeys.Control) {
					int index = this.SelectedIndex;
					if (e.Key == Key.PageUp) {
						if (++index >= this.Items.Count)
							index = 0;
					} else {
						if (--index < 0)
							index = this.Items.Count - 1;
					}
					parentWindow.SwitchView(index);

					e.Handled = true;
				}
			}
		}

		private void CreateViewTabControl()
		{
			if (viewTabControl == null) {
				viewTabControl = new TabControlWithModifiedShortcuts(this);
				viewTabControl.TabStripPlacement = System.Windows.Controls.Dock.Bottom;
				this.Content = viewTabControl;

				viewTabControl.SelectionChanged += delegate {
					UpdateActiveViewContent();
				};
			}
		}

		void ClearContent()
		{
			this.Content = null;
			if (viewTabControl != null) {
				foreach (TabItem page in viewTabControl.Items) {
					page.Content = null;
				}
				viewTabControl = null;
			}
		}

		void OnTitleNameChanged(object sender, EventArgs e)
		{
			if (sender == ActiveViewContent) {
				UpdateTitle();
			}
		}

		void OnInfoTipChanged(object sender, EventArgs e)
		{
			if (sender == ActiveViewContent) {
				UpdateInfoTip();
			}
		}

		void OnIsDirtyChanged(object sender, EventArgs e)
		{
			UpdateTitle();
			CommandManager.InvalidateRequerySuggested();
		}

		void UpdateTitleAndInfoTip()
		{
			UpdateInfoTip();
			UpdateTitle();
		}

		void UpdateInfoTip()
		{
			IViewContent content = ActiveViewContent;
			if (content != null) {
				string newInfoTip = content.InfoTip;
				if (!Equals(newInfoTip, this.ToolTip)) {
					this.ToolTip = newInfoTip;
					OnInfoTipChanged();
				}
			}
		}

		void UpdateTitle()
		{
			IViewContent content = ActiveViewContent;
			if (content != null) {
				string newTitle = content.TitleName;
				if (content.IsDirty)
					newTitle += "*";
				if (newTitle != Title) {
					Title = newTitle;
					OnTitleChanged();
				}
			}
		}

		void RegisterNewContent(IViewContent content)
		{
			Debug.Assert(content.WorkbenchWindow == null);
			content.WorkbenchWindow = this;

			content.TabPageTextChanged += OnTabPageTextChanged;
			content.TitleNameChanged += OnTitleNameChanged;
			content.InfoTipChanged += OnInfoTipChanged;
			content.IsDirtyChanged += OnIsDirtyChanged;

			this.dockLayout.Workbench.OnViewOpened(new ViewContentEventArgs(content));
		}

		void UnregisterContent(IViewContent content)
		{
			content.WorkbenchWindow = null;

			content.TabPageTextChanged -= OnTabPageTextChanged;
			content.TitleNameChanged -= OnTitleNameChanged;
			content.InfoTipChanged -= OnInfoTipChanged;
			content.IsDirtyChanged -= OnIsDirtyChanged;

			this.dockLayout.Workbench.OnViewClosed(new ViewContentEventArgs(content));
		}

		void OnTabPageTextChanged(object sender, EventArgs e)
		{
			RefreshTabPageTexts();
		}

		bool forceClose;

		public bool CloseWindow(bool force)
		{
			SD.MainThread.VerifyAccess();

			forceClose = force;
			var args = new CancelEventArgs();
			OnClosingEvent(this, args);
			if (!args.Cancel)
				OnClosedEvent(this, EventArgs.Empty);
			return this.ViewContents.Count == 0;
		}

		void OnClosingEvent(object sender, CancelEventArgs e)
		{
			if (!e.Cancel && !forceClose && this.IsDirty) {
				MessageBoxResult dr = MessageBox.Show(
					ResourceService.GetString("MainWindow.SaveChangesMessage"),
					ResourceService.GetString("MainWindow.SaveChangesMessageHeader") + " " + Title + " ?",
					MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
					MessageBoxResult.Yes);
				switch (dr) {
					case MessageBoxResult.Yes:
						foreach (IViewContent vc in this.ViewContents) {
							while (vc.IsDirty) {
								ICSharpCode.SharpDevelop.Commands.SaveFileHelper.Save(vc);
								if (vc.IsDirty) {
									if (MessageService.AskQuestion("${res:MainWindow.DiscardChangesMessage}")) {
										break;
									}
								}
							}
						}
						break;
					case MessageBoxResult.No:
						break;
					case MessageBoxResult.Cancel:
						e.Cancel = true;
						break;
				}
			}
			if (!e.Cancel) {
				foreach (IViewContent vc in this.viewContents) {
					dockLayout.Workbench.StoreMemento(vc);
				}
			}
		}

		void OnClosedEvent(object sender, EventArgs e)
		{
			Dispose();
			dockLayout.RemoveDocument(this);
			Closed?.Invoke(this, EventArgs.Empty);
			CommandManager.InvalidateRequerySuggested();
		}

		void RefreshTabPageTexts()
		{
			if (viewTabControl != null) {
				for (int i = 0; i < viewTabControl.Items.Count; ++i) {
					TabItem tabPage = (TabItem)viewTabControl.Items[i];
					tabPage.Header = StringParser.Parse(ViewContents[i].TabPageText);
				}
			}
		}

		void OnTitleChanged()
		{
			if (TitleChanged != null) {
				TitleChanged(this, EventArgs.Empty);
			}
		}

		public event EventHandler TitleChanged;

		void OnInfoTipChanged()
		{
			if (InfoTipChanged != null) {
				InfoTipChanged(this, EventArgs.Empty);
			}
		}

		public event EventHandler InfoTipChanged;

		public event EventHandler Closed;

		public override string ToString()
		{
			return "[AvalonWorkbenchWindow: " + this.Title + "]";
		}

		/// <summary>
		/// Gets the target for re-routing commands to this window.
		/// </summary>
		internal IInputElement GetCommandTarget()
		{
			IViewContent vc = ActiveViewContent;
			return vc != null ? vc.Control as IInputElement : null;
		}
	}
}
