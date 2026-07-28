using System.Windows.Media;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;

namespace ICSharpCode.SharpDevelop.Services;

// WPF-only rendering surface for ProjectBrowserNodeModel (see ProjectBrowserNodeModel.cs) - not
// linked into UnoDevelop, which computes its own icons/overlays in its WinUI tree converter
// instead of on the node model itself.
internal sealed partial class ProjectBrowserNodeModel
{
    public ImageSource Icon => ProjectBrowserIconService.GetIcon(this);

    public ImageSource LinkedFileOverlayIcon => Kind == ProjectBrowserNodeKind.LinkedFile
        ? PresentationResourceService.GetBitmapSource("ProjectBrowser.LinkedFileOverlay")
        : null;

    public ImageSource SourceControlOverlayIcon => ServiceSingleton.ServiceProvider.GetService<IProjectBrowserOverlayService>()?.GetOverlay(FullPath, IsDirectory);

    public string OverlayStatusKey => ServiceSingleton.ServiceProvider.GetService<IProjectBrowserOverlayService>()?.GetOverlayKey(FullPath, IsDirectory) ?? string.Empty;
}
