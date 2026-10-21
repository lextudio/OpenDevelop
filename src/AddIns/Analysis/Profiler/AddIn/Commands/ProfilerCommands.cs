using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.UnitTesting;
using Microsoft.Win32;
using OpenDevelop.Profiler.AddIn.Services;

namespace OpenDevelop.Profiler.AddIn.Commands;

/// <summary>Analysis &gt; Profiler &gt; Profile Current Project (CPU).</summary>
public sealed class ProfileProjectCommand : AbstractMenuCommand
{
	public override void Run() => CaptureCoordinator.ProfileCurrentProject();
}

/// <summary>Analysis &gt; Profiler &gt; Attach CPU Profiler to Process.</summary>
public sealed class AttachCpuProfilerCommand : AbstractMenuCommand
{
	public override void Run() => CaptureCoordinator.AttachCpu();
}

/// <summary>Analysis &gt; Profiler &gt; Capture Managed Heap Snapshot.</summary>
public sealed class CaptureHeapSnapshotCommand : AbstractMenuCommand
{
	public override void Run() => CaptureCoordinator.CaptureHeap();
}

/// <summary>Unit Tests pad &gt; Run with Profiler: samples the test host while the selected tests run.</summary>
public sealed class ProfileSelectedTestsCommand : AbstractMenuCommand
{
	public override void Run()
	{
		var testService = SD.GetRequiredService<ITestService>();
		var tests = TestableCondition.GetTests(testService.OpenSolution, Owner).ToList();
		CaptureCoordinator.ProfileTests(tests);
	}
}

/// <summary>Analysis &gt; Profiler &gt; Profiling Sessions.</summary>
public sealed class ShowProfilingSessionsCommand : AbstractMenuCommand
{
	public override void Run() => Dialogs.SessionsWindow.ShowWindow();
}

/// <summary>Analysis &gt; Profiler &gt; Open Profiling Report.</summary>
public sealed class OpenProfilingReportCommand : AbstractMenuCommand
{
	public override void Run()
	{
		var dialog = new OpenFileDialog
		{
			Filter = StringParser.Parse("${res:AddIns.Profiler.FileFilter.Capture}") + "|*.nettrace",
			CheckFileExists = true,
		};
		if (dialog.ShowDialog() == true)
			CaptureCoordinator.OpenReport(dialog.FileName);
	}
}
