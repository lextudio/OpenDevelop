// Clean-room extension bridging the VS SDK's imaging interop struct to the CPS-shaped
// ProjectImageMoniker used throughout the dependency model. Both are (Guid, int) key pairs.

using Microsoft.VisualStudio.Imaging.Interop;

namespace Microsoft.VisualStudio.ProjectSystem;

internal static class ImageMonikerExtensions
{
    public static ProjectImageMoniker ToProjectSystemType(this ImageMoniker moniker) =>
        new(moniker.Guid, moniker.Id);
}
