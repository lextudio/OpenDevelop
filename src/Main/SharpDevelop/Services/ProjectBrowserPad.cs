using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Services;

internal sealed class ProjectBrowserPad : AbstractPadContent
{
    private readonly ProjectBrowserViewModel viewModel;
    private readonly ProjectBrowserView view;

    public ProjectBrowserPad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<ProjectBrowserViewModel>();
        view = new ProjectBrowserView { DataContext = viewModel };
    }

    public override object Control => view;

    public override object InitiallyFocusedControl => view.InitiallyFocusedControl;

    public override void Dispose()
    {
        viewModel.Dispose();
    }
}
