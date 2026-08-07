// Copyright (c) 2026 AlphaSierraPapa for the SharpDevelop Team
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
using System.Windows.Input;

namespace ICSharpCode.SharpDevelop.ViewModels
{
	/// <summary>
	/// Shell-wide "show a message with an optional single action" surface (doc/technotes/ilspy.md
	/// "Follow-on infrastructure: a shell-wide notification banner", 2026-08-07) - the thing between
	/// a status-bar message (cheap, no action, easy to miss) and a modal dialog (interrupts). First
	/// consumer is the manual "Check for Updates" command (doc/technotes/auto-update.md), but this
	/// type carries no update-specific state on purpose so any future caller (extension-install
	/// prompts, solution-reload notices, ...) can reuse the same one instance.
	///
	/// Registered via <c>SD.Services.AddService(typeof(INotificationHost), this)</c> by
	/// <c>WpfWorkbench</c>'s constructor and resolved via
	/// <c>SD.Services.GetService(typeof(INotificationHost))</c> by any caller - the same
	/// service-indirection pattern as <see cref="IPaneModelHost"/>/<c>IPropertyPadHost</c>/
	/// <c>IOutputPadHost</c>, so callers never need a compile-time reference to the App project.
	/// </summary>
	public interface INotificationHost
	{
		/// <summary>
		/// Shows the banner with the given message and, if <paramref name="actionText"/> and
		/// <paramref name="action"/> are both non-null, a single action button. Replaces whatever
		/// the banner was previously showing - there is one shared banner, not a queue.
		/// </summary>
		void Show(string message, string actionText, Action action);

		/// <summary>Hides the banner, if visible.</summary>
		void Dismiss();
	}

	public sealed class NotificationBannerViewModel : ObservableObjectBase, INotificationHost
	{
		sealed class RelayCommand : ICommand
		{
			readonly Action action;

			public RelayCommand(Action action)
			{
				this.action = action;
			}

			public event EventHandler CanExecuteChanged { add { } remove { } }

			public bool CanExecute(object parameter) => action != null;

			public void Execute(object parameter) => action?.Invoke();
		}

		bool isVisible;

		public bool IsVisible {
			get => isVisible;
			set => SetProperty(ref isVisible, value);
		}

		string message;

		public string Message {
			get => message;
			private set => SetProperty(ref message, value);
		}

		string actionText;

		public string ActionText {
			get => actionText;
			private set {
				if (SetProperty(ref actionText, value))
					OnPropertyChanged(nameof(HasAction));
			}
		}

		/// <summary>Bound to the action button's Visibility - WPF has no built-in null-to-bool
		/// converter, and this avoids introducing one just for this.</summary>
		public bool HasAction => actionText != null;

		ICommand actionCommand;

		public ICommand ActionCommand {
			get => actionCommand;
			private set => SetProperty(ref actionCommand, value);
		}

		public ICommand DismissCommand { get; }

		public NotificationBannerViewModel()
		{
			DismissCommand = new RelayCommand(Dismiss);
		}

		public void Show(string message, string actionText, Action action)
		{
			Message = message;
			ActionText = actionText;
			// Dismiss the banner as part of running the action - matches ILSpy's
			// UpdatePanelViewModel.DownloadOrCheckUpdate (IsPanelVisible = false before acting).
			ActionCommand = (actionText != null && action != null)
				? new RelayCommand(() => { Dismiss(); action(); })
				: null;
			IsVisible = true;
		}

		public void Dismiss()
		{
			IsVisible = false;
		}
	}
}
