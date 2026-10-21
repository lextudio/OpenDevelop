using System.Windows;
using System.Windows.Controls;
using ICSharpCode.Core;
using OpenDevelop.Profiler.Cpu;
using OpenDevelop.Profiler.Heap;
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
			var size = new FileInfo(path).Length;
			// What a dump costs to read follows its managed heap, not its size on disk, which also holds every native page: ten times the
			// threshold avoids asking about every ordinary dump.
			var threshold = ProfilerSettings.LargeCaptureBytes * (await Task.Run(() => ReportLoader.Detect(path)) == CaptureKind.Dump ? 10 : 1);
			if (ReportLoader.IsLarge(size, threshold) && !MessageService.AskQuestion(
					StringParser.Format("${res:AddIns.Profiler.Report.LargeCapture}", ReportFormat.Bytes(size))))
			{
				Content = Message(StringParser.Parse("${res:AddIns.Profiler.Report.NotOpened}"));
				return;
			}

			// Everything that takes time happens here, off the UI thread: parsing, and the call tree the report opens on.
			var assemblies = ProfilerWorkspace.SolutionAssemblyNames();
			var (report, userCode, tree) = await Task.Run(() =>
			{
				var loaded = ReportLoader.Load(path);
				if (loaded.Cpu is not { } cpu)
					return (loaded, null, null);
				var isUserCode = UserCode.ForProfile(cpu, assemblies);
				return (loaded, isUserCode, cpu.BuildCallTree(CpuReportView.DefaultFilter(isUserCode)));
			});
			Content = report.Kind switch
			{
				CaptureKind.Cpu or CaptureKind.Allocation => new CpuReportView(report.Cpu!, assemblies, report.Counters, userCode, tree),
				CaptureKind.Heap => new HeapReportView(report.Heap!, ContentInsights.TryLoad(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? string.Empty, ContentInsights.FileName))),
				CaptureKind.Dump => new DumpReportView(report.Dump!),
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
