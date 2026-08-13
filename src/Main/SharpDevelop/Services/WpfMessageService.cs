using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Services;

/// <summary>WPF-backed application message service with the workbench as dialog owner.</summary>
sealed class WpfMessageService : IMessageService
{
	// Integration-test runs (OpenDevelopAppFixture sets OD_TEST_MODE=1) have nobody to click a
	// modal dialog: showing one would hang the run/CI forever, so every dialog in this service
	// is skipped in favor of a safe default answer, logged instead of displayed.
	static bool IsTestMode => TestMode.IsActive;

	Dispatcher dispatcher;

	public void Attach(Dispatcher dispatcher, Window dialogOwner)
	{
		this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
		DialogOwner = dialogOwner;
	}

	public Window DialogOwner { get; set; }
	public string DefaultMessageBoxTitle => ProductName;
	public string ProductName => "OpenDevelop";

	T Invoke<T>(Func<T> action) => dispatcher == null || dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);
	void Invoke(Action action)
	{
		if (dispatcher == null || dispatcher.CheckAccess())
			action();
		else
			dispatcher.Invoke(action);
	}

	static MessageBoxResult DefaultResult(MessageBoxButton buttons) => buttons switch {
		MessageBoxButton.OK => MessageBoxResult.OK,
		MessageBoxButton.YesNo => MessageBoxResult.No,
		MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
		_ => MessageBoxResult.Cancel
	};

	MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
	{
		if (IsTestMode) {
			var result = DefaultResult(buttons);
			LoggingService.Info($"OD_TEST_MODE: suppressed dialog \"{StringParser.Parse(caption)}\" ({StringParser.Parse(message)}), auto-answered {result}");
			return result;
		}
		return Invoke(() => GetDialogOwner() is Window owner
			? MessageBox.Show(owner, StringParser.Parse(message), StringParser.Parse(caption), buttons, image)
			: MessageBox.Show(StringParser.Parse(message), StringParser.Parse(caption), buttons, image));
	}

	Window GetDialogOwner()
	{
		Window owner = DialogOwner;
		if (Application.Current != null) {
			foreach (Window window in Application.Current.Windows) {
				if (window.IsActive) {
					owner = window;
					break;
				}
			}
		}
		return owner;
	}

	public void ShowError(string message)
	{
		LoggingService.Error(message);
		Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
	}

	public void ShowWarning(string message)
	{
		LoggingService.Warn(message);
		Show(message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
	}

	public void ShowMessage(string message, string caption = null)
	{
		LoggingService.Info(message);
		Show(message, caption ?? DefaultMessageBoxTitle, MessageBoxButton.OK, MessageBoxImage.Information);
	}

	public void ShowException(Exception ex, string message = null)
	{
		LoggingService.Error(message, ex);
		Show(CombineException(message, ex, includeDetails: true), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
	}

	public void ShowHandledException(Exception ex, string message = null)
	{
		LoggingService.Warn(message, ex);
		Show(CombineException(message, ex, includeDetails: false), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
	}

	static string CombineException(string message, Exception ex, bool includeDetails)
	{
		string exceptionText = includeDetails ? ex?.ToString() : ex?.Message;
		if (string.IsNullOrEmpty(message))
			return exceptionText ?? "An unknown error occurred.";
		return string.IsNullOrEmpty(exceptionText) ? message : message + "\n\n" + exceptionText;
	}

	public void ShowErrorFormatted(string formatstring, params object[] formatitems) =>
		ShowError(StringParser.Format(formatstring, formatitems));

	public void ShowWarningFormatted(string formatstring, params object[] formatitems) =>
		ShowWarning(StringParser.Format(formatstring, formatitems));

	public void ShowMessageFormatted(string formatstring, string caption, params object[] formatitems) =>
		ShowMessage(StringParser.Format(formatstring, formatitems), caption);

	public bool AskQuestion(string question, string caption = null) =>
		Show(question, caption ?? "Question", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

	public int ShowCustomDialog(string caption, string dialogText, int acceptButtonIndex, int cancelButtonIndex, params string[] buttontexts)
	{
		if (IsTestMode) {
			LoggingService.Info($"OD_TEST_MODE: suppressed custom dialog \"{StringParser.Parse(caption)}\" ({StringParser.Parse(dialogText)}), auto-answered {cancelButtonIndex}");
			return cancelButtonIndex;
		}
		return Invoke(() => ShowButtonDialog(caption, dialogText, acceptButtonIndex, cancelButtonIndex, buttontexts));
	}

	int ShowButtonDialog(string caption, string dialogText, int acceptButtonIndex, int cancelButtonIndex, string[] buttontexts)
	{
		int result = -1;
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		var window = CreateDialog(caption, dialogText, buttons);
		for (int index = 0; index < buttontexts.Length; index++) {
			int selectedIndex = index;
			var button = new Button {
				Content = StringParser.Parse(buttontexts[index]), MinWidth = 80, Margin = new Thickness(4, 0, 0, 0),
				IsDefault = index == acceptButtonIndex, IsCancel = index == cancelButtonIndex
			};
			button.Click += (_, _) => { result = selectedIndex; window.DialogResult = true; };
			buttons.Children.Add(button);
		}
		window.ShowDialog();
		return result;
	}

	public string ShowInputBox(string caption, string dialogText, string defaultValue)
	{
		if (IsTestMode) {
			LoggingService.Info($"OD_TEST_MODE: suppressed input box \"{StringParser.Parse(caption)}\" ({StringParser.Parse(dialogText)}), auto-answered null (cancel)");
			return null;
		}
		return Invoke(() => {
		var textBox = new TextBox { Text = defaultValue ?? string.Empty, MinWidth = 360, Margin = new Thickness(0, 8, 0, 12) };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		var content = new StackPanel();
		content.Children.Add(new TextBlock { Text = StringParser.Parse(dialogText), TextWrapping = TextWrapping.Wrap });
		content.Children.Add(textBox);
		content.Children.Add(buttons);
		var window = CreateDialog(caption, content);
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(4, 0, 0, 0) };
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(4, 0, 0, 0) };
		ok.Click += (_, _) => window.DialogResult = true;
		buttons.Children.Add(ok);
		buttons.Children.Add(cancel);
		return window.ShowDialog() == true ? textBox.Text : null;
		});
	}

	Window CreateDialog(string caption, string text, UIElement buttons)
	{
		var panel = new StackPanel();
		panel.Children.Add(new TextBlock { Text = StringParser.Parse(text), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
		panel.Children.Add(buttons);
		return CreateDialog(caption, panel);
	}

	Window CreateDialog(string caption, UIElement content)
	{
		var owner = GetDialogOwner();
		return new Window {
			Title = StringParser.Parse(caption ?? DefaultMessageBoxTitle), Owner = owner,
			WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
			SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 420, MaxWidth = 720,
			Content = new Border { Padding = new Thickness(20), Child = content }, ShowInTaskbar = false
		};
	}

	public void InformSaveError(FileName fileName, string message, string dialogName, Exception exceptionGot) =>
		ShowError(CombineException($"{ResolveSaveErrorMessage(fileName, message, exceptionGot)}\n\n{fileName}", exceptionGot, includeDetails: false));

	public ChooseSaveErrorResult ChooseSaveError(FileName fileName, string message, string dialogName, Exception exceptionGot, bool chooseLocationEnabled)
	{
		var result = Show(CombineException($"{ResolveSaveErrorMessage(fileName, message, exceptionGot)}\n\n{fileName}", exceptionGot, includeDetails: false),
			dialogName ?? "Save Error", MessageBoxButton.YesNo, MessageBoxImage.Error);
		return result == MessageBoxResult.Yes ? ChooseSaveErrorResult.Retry : ChooseSaveErrorResult.Ignore;
	}

	// message comes in as a raw resource template (e.g. FileUtilityService.CantLoadFileStandardText,
	// "Can't load file ${FileNameWithoutPath} under ${Path}.") - the WinForms SaveErrorInformDialog
	// this replaced used to run it through StringParser.Parse with these same tags before display;
	// this port dropped that step, so the dialog showed the literal "${FileNameWithoutPath}" text.
	static string ResolveSaveErrorMessage(FileName fileName, string message, Exception exceptionGot) =>
		StringParser.Parse(message,
			new StringTagPair("FileName", fileName),
			new StringTagPair("Path", System.IO.Path.GetDirectoryName(fileName)),
			new StringTagPair("FileNameWithoutPath", System.IO.Path.GetFileName(fileName)),
			new StringTagPair("Exception", exceptionGot.GetType().FullName));
}
