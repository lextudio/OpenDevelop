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

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>
	/// Broadcast via <see cref="ICSharpCode.ILSpy.Util.MessageBus{T}"/> (the primitive is
	/// source-linked into ICSharpCode.Core - see that project - so the shell publishes without
	/// referencing the ILSpy AddIn, which references the shell) whenever the docking layer's own
	/// raw active content changes - a tool pane (e.g. the Projects pad's <c>ToolPaneModel</c>) or a
	/// document window, whichever the user last focused. Unlike <see cref="IWorkbenchLayout.ActiveContent"/>
	/// (gated by WpfWorkbench.UpdateActiveTracking to only ever reflect a DOCUMENT, so that
	/// ActiveViewContent survives switching focus to a tool pad - see its doc comment), this always
	/// reflects the true dock-level value, letting any subscriber (in this assembly or any AddIn
	/// that references it, without depending on the concrete workbench-layout implementation) react
	/// to a tool pane becoming active too.
	/// </summary>
	public sealed class ActiveDockContentChangedEventArgs : EventArgs
	{
		public object Content { get; }

		public ActiveDockContentChangedEventArgs(object content)
		{
			Content = content;
		}
	}

	/// <summary>
	/// The IWorkbenchLayout object is responsible for the layout of
	/// the workspace, it shows the contents, chooses the IWorkbenchWindow
	/// implementation etc. it could be attached/detached at the runtime
	/// to a workbench.
	/// </summary>
	interface IWorkbenchLayout
	{
		/// <summary>
		/// The active workbench window.
		/// </summary>
		IWorkbenchWindow ActiveWorkbenchWindow {
			get;
		}
		
		/// <summary>
		/// Gets the open workbench windows.
		/// </summary>
		IList<IWorkbenchWindow> WorkbenchWindows {
			get;
		}
		
		/// <summary>
		/// The active content. This can be either a IViewContent or a IPadContent, depending on
		/// where the focus currently is.
		/// </summary>
		IServiceProvider ActiveContent {
			get;
		}

		/// <summary>
		/// The docking layer's own raw active content - a tool pane's bound model (e.g. the Projects
		/// pad's ToolPaneModel) or a document window, whichever the user last focused. Unlike
		/// <see cref="ActiveContent"/> (deliberately document-gated, see its doc comment), this
		/// reflects a focused tool pane too.
		///
		/// Exposed as a readable property as well as via the
		/// <see cref="ICSharpCode.ILSpy.Util.MessageBus{T}"/> broadcast (ActiveDockContentChangedEventArgs)
		/// because a consumer that starts caring about the active content *after* the user already
		/// activated the pane would otherwise never see a change notification at all - the broadcast
		/// only fires on a change, so it cannot answer "what is active right now?".
		/// </summary>
		object ActiveDockContent {
			get;
		}

		event EventHandler ActiveWorkbenchWindowChanged;
		event EventHandler ActiveContentChanged;
		
		/// <summary>
		/// Attaches this layout manager to a workbench object.
		/// </summary>
		void Attach(IWorkbench workbench);
		
		/// <summary>
		/// Detaches this layout manager from the current workspace.
		/// </summary>
		void Detach();
		
		/// <summary>
		/// Shows a new <see cref="IPadContent"/>.
		/// </summary>
		void ShowPad(PadDescriptor padDescriptor);
		
		/// <summary>
		/// Activates a pad (Show only makes it visible but Activate does
		/// bring it to foreground)
		/// </summary>
		void ActivatePad(PadDescriptor padDescriptor);
		
		/// <summary>
		/// Hides a <see cref="IPadContent"/>.
		/// </summary>
		void HidePad(PadDescriptor padDescriptor);
		
		/// <summary>
		/// Closes and disposes a <see cref="IPadContent"/>.
		/// </summary>
		void UnloadPad(PadDescriptor padDescriptor);
		
		/// <summary>
		/// Returns true, if the pad header is visible (the pad content doesn't need to be visible).
		/// </summary>
		bool IsVisible(PadDescriptor padDescriptor);
		
		/// <summary>
		/// Shows a new <see cref="IViewContent"/> and optionally switches to it.
		/// </summary>
		IWorkbenchWindow ShowView(IViewContent content, bool switchToOpenedView);
		
		void LoadConfiguration();
		void StoreConfiguration();
		
		void SwitchLayout(string layoutName);
	}
}
