using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>A capture file shown as a workbench document.</summary>
public sealed class ProfilerReportViewContent : AbstractViewContent
{
	readonly OpenedFile file;
	readonly ProfilerReportControl control;

	public ProfilerReportViewContent(OpenedFile file)
	{
		// The report reads the file itself and never edits it, so it is not registered on the
		// workbench's file (which would make the document look dirty or saveable).
		this.file = file;
		control = new ProfilerReportControl(file.FileName.ToString());
		TabPageText = Path.GetFileName(file.FileName.ToString());
		TitleName = TabPageText;
		FileService.FileRemoving += OnFileRemoving;
	}

	public override object Control => control;

	internal ProfilerReportControl ReportControl => control;

	public override FileName PrimaryFileName => file.FileName;

	void OnFileRemoving(object? sender, FileCancelEventArgs e)
	{
		if (FileUtility.IsEqualFileName(e.FileName, file.FileName) || FileUtility.IsBaseDirectory(e.FileName, file.FileName))
			WorkbenchWindow.CloseWindow(true);
	}

	public override void Dispose()
	{
		FileService.FileRemoving -= OnFileRemoving;
		base.Dispose();
	}
}
