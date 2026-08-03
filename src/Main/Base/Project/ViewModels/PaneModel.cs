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

using System;
using System.ComponentModel;
using System.Windows.Input;

namespace ICSharpCode.SharpDevelop.ViewModels
{
	/// <summary>
	/// The one thing <see cref="PaneModel"/>'s <c>CloseCommand</c> needs from the actual docking
	/// host (doc/technotes/ilspy.md "Docking and layout replacement" item 1/item 4
	/// consolidation, 2026-08-03) - split out so <c>PaneModel</c>/<c>ToolPaneModel</c> can live in
	/// the Base project (reachable from every AddIn) without depending on <c>DockWorkspace</c>,
	/// which is internal to the App project (<c>OpenDevelop.dll</c>) and only ever has one real
	/// implementation. Registered via <c>SD.Services.AddService(typeof(IPaneModelHost), this)</c>
	/// by <c>DockWorkspace</c>'s constructor - the same "shell owns the mechanism, resolved
	/// through the service container" pattern already used throughout this codebase (see
	/// <c>IWorkbench</c>, <c>IStatusBarService</c>, etc.), not a new one invented for this.
	/// </summary>
	public interface IPaneModelHost
	{
		void Remove(PaneModel model);
	}

	public abstract class PaneModel : ObservableObjectBase
	{
		protected PaneModel()
		{
		}

		class CloseCommandImpl : ICommand
		{
			readonly PaneModel model;

			public CloseCommandImpl(PaneModel model)
			{
				this.model = model;
				this.model.PropertyChanged += Model_PropertyChanged;
			}

			private void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
			{
				if (e.PropertyName == nameof(model.IsCloseable))
				{
					CanExecuteChanged?.Invoke(this, EventArgs.Empty);
				}
			}

			public event EventHandler CanExecuteChanged;

			public bool CanExecute(object parameter)
			{
				return model.IsCloseable;
			}

			public void Execute(object parameter)
			{
				(SD.Services.GetService(typeof(IPaneModelHost)) as IPaneModelHost)?.Remove(model);
			}
		}

		private bool isSelected;

		public bool IsSelected {
			get => isSelected;
			set => SetProperty(ref isSelected, value);
		}

		private bool isActive;

		public bool IsActive {
			get => isActive;
			set => SetProperty(ref isActive, value);
		}

		private bool isVisible;

		public bool IsVisible {
			get { return isVisible; }
			set {
				if (SetProperty(ref isVisible, value) && !value)
				{
					// When the pane is hidden, it should no longer be marked as active, else it won't raise an event when it is activated again.
					IsActive = false;
				}
			}
		}

		private bool isCloseable = true;

		public bool IsCloseable {
			get => isCloseable;
			set => SetProperty(ref isCloseable, value);
		}

		public ICommand CloseCommand => new CloseCommandImpl(this);

		private string contentId;

		public string ContentId {
			get => contentId;
			set => SetProperty(ref contentId, value);
		}

		private string title;

		public string Title {
			get => title;
			set => SetProperty(ref title, value);
		}
	}
}
