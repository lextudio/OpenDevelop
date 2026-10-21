using System.Windows;
using System.Windows.Controls;
using ICSharpCode.Core;
using OpenDevelop.Profiler.Collection;

namespace OpenDevelop.Profiler.AddIn.Dialogs;

public enum ProcessPickerPurpose
{
	Cpu,
	HeapSnapshot,
}

/// <summary>Lets the user pick a running .NET process to sample or to take a heap snapshot of.</summary>
public partial class ProcessPickerWindow : Window
{
	public ProcessPickerWindow(ProcessPickerPurpose purpose)
	{
		InitializeComponent();
		if (purpose == ProcessPickerPurpose.HeapSnapshot)
		{
			prompt.Text = StringParser.Parse("${res:AddIns.Profiler.Attach.HeapPrompt}");
			warning.Text = StringParser.Parse("${res:AddIns.Profiler.Attach.HeapWarning}");
			warning.Visibility = Visibility.Visible;
			startButton.Content = StringParser.Parse("${res:AddIns.Profiler.Attach.TakeSnapshot}");
		}
		else
		{
			prompt.Text = StringParser.Parse("${res:AddIns.Profiler.Attach.CpuPrompt}");
			startButton.Content = StringParser.Parse("${res:AddIns.Profiler.Attach.StartSampling}");
		}
		Loaded += async (_, _) => await RefreshAsync();
	}

	/// <summary>The process the user chose, or <c>null</c> if the dialog was cancelled.</summary>
	public ManagedProcess? SelectedProcess { get; private set; }

	/// <summary>Shows the dialog over the main window and returns the chosen process, if any.</summary>
	public static ManagedProcess? Pick(Window? owner, ProcessPickerPurpose purpose)
	{
		var window = new ProcessPickerWindow(purpose) { Owner = owner };
		return window.ShowDialog() == true ? window.SelectedProcess : null;
	}

	async Task RefreshAsync()
	{
		refreshButton.IsEnabled = false;
		try
		{
			// Asking each process for its diagnostic port can be slow on a busy machine.
			var processes = await Task.Run(ProcessTargets.ListManagedProcesses);
			processList.ItemsSource = processes;
			if (processes.Count == 0)
				prompt.Text += Environment.NewLine + StringParser.Parse("${res:AddIns.Profiler.Attach.NoProcesses}");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			MessageService.ShowError(exception.Message);
		}
		finally
		{
			refreshButton.IsEnabled = true;
		}
	}

	void Confirm()
	{
		if (processList.SelectedItem is not ManagedProcess process)
			return;
		SelectedProcess = process;
		DialogResult = true;
	}

	async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

	void Start_Click(object sender, RoutedEventArgs e) => Confirm();

	void ProcessList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Confirm();

	void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
		startButton.IsEnabled = processList.SelectedItem is ManagedProcess;
}
