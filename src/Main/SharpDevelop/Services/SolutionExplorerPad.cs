using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Services;

internal sealed class SolutionExplorerPad : AbstractPadContent
{
    private readonly SolutionExplorerViewModel viewModel;
    private readonly SolutionExplorerView view;

    public SolutionExplorerPad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<SolutionExplorerViewModel>();
        view = new SolutionExplorerView { DataContext = viewModel };
    }

    public override object Control => view;

    public override object InitiallyFocusedControl => view.InitiallyFocusedControl;

    public override void Dispose()
    {
        viewModel.Dispose();
    }
}
