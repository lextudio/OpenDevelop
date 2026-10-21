using System.Windows;
using System.Windows.Controls;
using ICSharpCode.Core;
using OpenDevelop.Profiler.AddIn.Services;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// Hosts one capture file: shows "loading" while the file is parsed off the UI thread, then the CPU or
/// heap report, or the reason the file could not be shown.
/// </summary>
public sealed class ProfilerReportControl : ContentControl
{
	readonly string path;
	readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
	bool started;

	public ProfilerReportControl(string path)
	{
		this.path = path;
		Content = Message(StringParser.Format("${res:AddIns.Profiler.Report.Loading}", System.IO.Path.GetFileName(path)));
		Loaded += OnLoaded;
	}

	/// <summary>Completes once the file has been parsed and its report (or the failure message) is shown.</summary>
	internal Task Ready => ready.Task;

	/// <summary>The report view now shown, or the message that replaced it.</summary>
	internal object? ReportView => Content;

	async void OnLoaded(object sender, RoutedEventArgs e)
	{
		// Loaded fires again whenever the document tab is re-attached; parse only once.
		if (started)
			return;
		started = true;
		try
		{
			var report = await Task.Run(() => ReportLoader.Load(path));
			Content = report.Kind switch
			{
				CaptureKind.Cpu => new CpuReportView(report.Cpu!, ProfilerWorkspace.SolutionAssemblyNames()),
				CaptureKind.Heap => new HeapReportView(report.Heap!),
				_ => Message(string.Empty),
			};
		}
		catch (Exception exception) when (exception is ProfilingException or IOException)
		{
			LoggingService.Warn("Could not open the profiler report " + path, exception);
			Content = Message(StringParser.Format("${res:AddIns.Profiler.Report.LoadFailed}", exception.Message));
		}
		finally
		{
			ready.TrySetResult();
		}
	}

	static TextBlock Message(string text) => new()
	{
		Text = text,
		Margin = new Thickness(16),
		TextWrapping = TextWrapping.Wrap,
	};
}
