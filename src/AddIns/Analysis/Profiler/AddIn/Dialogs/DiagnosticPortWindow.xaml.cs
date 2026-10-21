using System.Windows;
using System.Windows.Controls;
using OpenDevelop.Profiler.Collection;

namespace OpenDevelop.Profiler.AddIn.Dialogs;

/// <summary>What the user asked for in the Attach to Diagnostic Port dialog.</summary>
public sealed record DiagnosticPortRequest(string Address, CaptureMode Mode);

/// <summary>
/// Asks for the address to listen at and what to record, and shows the environment variable to start the program with. The
/// program, wherever it runs, connects to that address; nothing here starts or finds a process.
/// </summary>
public partial class DiagnosticPortWindow : Window
{
	public DiagnosticPortWindow()
	{
		InitializeComponent();
		addressBox.Text = DiagnosticPortListener.SuggestAddress();
	}

	public DiagnosticPortRequest? Request { get; private set; }

	public static DiagnosticPortRequest? Ask(Window? owner)
	{
		var window = new DiagnosticPortWindow { Owner = owner };
		return window.ShowDialog() == true ? window.Request : null;
	}

	void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		var address = addressBox.Text.Trim();
		commandBox.Text = "DOTNET_DiagnosticPorts=" + address + ",suspend";
		listenButton.IsEnabled = address.Length > 0;
	}

	void Listen_Click(object sender, RoutedEventArgs e)
	{
		Request = new DiagnosticPortRequest(
			addressBox.Text.Trim(), allocationMode.IsChecked == true ? CaptureMode.Allocation : CaptureMode.Cpu);
		DialogResult = true;
	}
}
