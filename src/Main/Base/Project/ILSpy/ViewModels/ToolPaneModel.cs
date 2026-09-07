// Copyright (c) 2019 AlphaSierraPapa for the SharpDevelop Team
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

using System.Windows.Input;

namespace ICSharpCode.ILSpy.ViewModels
{
	/// <summary>
	/// Host-neutral layout hint for which side of the workbench a pane prefers to dock to.
	/// </summary>
	public enum PreferredDockSide
	{
		Left,
		Right,
		Top,
		Bottom,
	}

	/// <summary>
	/// Service interface for close dispatch — avoids coupling PaneModel to a specific
	/// DockWorkspace. Registered by the hosting shell via
	/// <c>SD.Services.AddService(typeof(IPaneModelHost), this)</c>.
	/// </summary>
	public interface IPaneModelHost
	{
		void Remove(PaneModel model);
		void Add(ToolPaneModel model);
	}

#if CROSS_PLATFORM
	public abstract class ToolPaneModel : Dock.Model.TomsToolbox.Controls.Tool
	{
		protected static DockWorkspace DockWorkspace => App.ExportProvider.GetExportedValue<DockWorkspace>();
#else
	public abstract class ToolPaneModel : PaneModel
	{
#endif
		public virtual void Show()
		{
			this.IsActive = true;
			this.IsVisible = true;
#if CROSS_PLATFORM
			DockWorkspace.ActivateToolPane(ContentId);
#endif
		}

		public KeyGesture ShortcutKey { get; protected set; }

		public string Icon { get; protected set; }

		public ICommand AssociatedCommand { get; set; }

		public object Content { get; protected set; }

		/// <summary>
		/// Preferred initial docked size in DIPs along the pane's docking axis.
		/// Null means "no preference, let the layout default apply".
		/// </summary>
		public double? PreferredDockSize { get; protected set; }

		/// <summary>
		/// Preferred side of the workbench to dock to.
		/// </summary>
		public PreferredDockSide? PreferredDockSide { get; protected set; }

		/// <summary>
		/// Fully-qualified class name of the legacy AddInTree &lt;Pad&gt; this model replaces, if any.
		/// </summary>
		public string LegacyPadClass { get; protected set; }
	}
}
