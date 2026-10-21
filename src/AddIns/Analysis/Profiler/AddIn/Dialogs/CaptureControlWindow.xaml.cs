using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace OpenDevelop.Profiler.AddIn.Dialogs;

/// <summary>
/// The small window shown while a capture runs: what is being captured, how long it has been going, and
/// the one action that ends it (Stop for a CPU session, Cancel while a heap snapshot is taken).
/// Closing the window with its close button counts as pressing that action.
/// </summary>
public partial class CaptureControlWindow : Window
{
	readonly Stopwatch clock = Stopwatch.StartNew();
	readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
	readonly string statusFormat;
	bool allowClose;

	/// <param name="title">Window title, normally the target's name.</param>
	/// <param name="statusFormat">Status line; <c>{0}</c> is replaced by the elapsed time as mm:ss.</param>
	/// <param name="hint">Optional advice shown under the status line.</param>
	/// <param name="actionText">Text of the button that ends the capture.</param>
	public CaptureControlWindow(string title, string statusFormat, string? hint, string actionText)
	{
		InitializeComponent();
		Title = title;
		this.statusFormat = statusFormat;
		actionButton.Content = actionText;
		if (!string.IsNullOrEmpty(hint))
		{
			this.hint.Text = hint;
			this.hint.Visibility = Visibility.Visible;
		}
		UpdateStatus();
		timer.Tick += (_, _) => UpdateStatus();
		timer.Start();
	}

	/// <summary>Raised once when the user asks to end the capture.</summary>
	public event EventHandler? ActionRequested;

	/// <summary>Replaces the status line and stops further action requests, e.g. while a stop is finishing.</summary>
	public void SetBusy(string text)
	{
		timer.Stop();
		status.Text = text;
		actionButton.IsEnabled = false;
	}

	/// <summary>Closes the window without treating it as a user action.</summary>
	public void CloseFromCode()
	{
		timer.Stop();
		allowClose = true;
		Close();
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (!allowClose)
		{
			e.Cancel = true;
			RequestAction();
		}
		base.OnClosing(e);
	}

	void UpdateStatus() => status.Text = string.Format(statusFormat, clock.Elapsed.ToString(@"mm\:ss"));

	void RequestAction()
	{
		if (!actionButton.IsEnabled)
			return;
		actionButton.IsEnabled = false;
		ActionRequested?.Invoke(this, EventArgs.Empty);
	}

	void ActionButton_Click(object sender, RoutedEventArgs e) => RequestAction();
}
