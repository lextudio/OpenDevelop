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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using AvalonDock.Layout;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.SharpDevelop.Workbench
{
	// Migrated from the SharpDevelop-patched AvalonDock 1.3 fork (subclassed DockableContent) to
	// Dirkster99/AvalonDock (lextudio fork): a pad is now a LayoutAnchorable subclass whose .Content
	// hosts the pad control. Placement uses AddToLayout(AnchorableShowStrategy) instead of
	// Show(dockingManager, AnchorStyle); focus-on-activate rides IsActiveChanged instead of the old
	// FocusContent() override (LayoutContent is a data model, not a UIElement).
	sealed class AvalonPadContent : LayoutAnchorable, IDisposable
	{
		PadDescriptor descriptor;
		IPadContent padInstance;
		AvalonDockLayout layout;
		TextBlock placeholder;

		public IPadContent PadContent {
			get { return padInstance; }
		}

		public AvalonPadContent(AvalonDockLayout layout, PadDescriptor descriptor)
		{
			this.descriptor = descriptor;
			this.layout = layout;

			this.ContentId = descriptor.Class;
			this.Title = StringParser.Parse(descriptor.Title);
			this.CanClose = false; // pads are hidden, not closed
			placeholder = new TextBlock { Text = this.Title };
			this.Content = placeholder;
			this.IconSource = PresentationResourceService.GetBitmapSource(descriptor.Icon);

			this.IsActiveChanged += OnIsActiveChanged;
			placeholder.IsVisibleChanged += AvalonPadContent_IsVisibleChanged;
		}

		void OnIsActiveChanged(object sender, EventArgs e)
		{
			if (!IsActive)
				return;
			LoadPadContentIfRequired();
			if (padInstance != null && padInstance.InitiallyFocusedControl is IInputElement focus) {
				Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded,
				                                         new Action(delegate { Keyboard.Focus(focus); }));
			}
		}

		AnchorableShowStrategy GetShowStrategy()
		{
			if ((descriptor.DefaultPosition & DefaultPadPositions.Top) != 0)
				return AnchorableShowStrategy.Top;
			if ((descriptor.DefaultPosition & DefaultPadPositions.Left) != 0)
				return AnchorableShowStrategy.Left;
			if ((descriptor.DefaultPosition & DefaultPadPositions.Bottom) != 0)
				return AnchorableShowStrategy.Bottom;
			return AnchorableShowStrategy.Right;
		}

		public void ShowInDefaultPosition()
		{
			AddToLayout(layout.DockingManager, GetShowStrategy());
			if ((descriptor.DefaultPosition & DefaultPadPositions.Hidden) != 0)
				Hide();
		}

		void AvalonPadContent_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			placeholder.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(LoadPadContentIfRequired));
		}

		internal void LoadPadContentIfRequired()
		{
			bool dockingManagerIsInitializing = layout.Busy || !layout.DockingManager.IsLoaded;
			if (placeholder != null && placeholder.IsVisible && !dockingManagerIsInitializing) {
				placeholder.IsVisibleChanged -= AvalonPadContent_IsVisibleChanged;
				padInstance = descriptor.PadContent;
				if (padInstance != null) {
					this.Content = padInstance.Control;
					placeholder = null;
				}
			}
		}

		public void Dispose()
		{
			if (padInstance != null) {
				padInstance.Dispose();
			}
		}

		public override string ToString()
		{
			return "[AvalonPadContent " + this.ContentId + "]";
		}
	}
}
