// Clean-room reimplementation. See docs/project-system.md and memory/opensource-cps-shim.md.

using System;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>
/// Identifies an image in a VS image list (image service key = GUID + index).
/// Mirrors the CPS struct surface used by dotnet/project-system.
/// </summary>
public readonly struct ProjectImageMoniker : IEquatable<ProjectImageMoniker>
{
    public static readonly ProjectImageMoniker Empty = default;

    public Guid ImageListGuid { get; }
    public int ImageIndex { get; }

    public ProjectImageMoniker(Guid imageListGuid, int imageIndex)
    {
        ImageListGuid = imageListGuid;
        ImageIndex = imageIndex;
    }

    public bool IsEmpty => ImageListGuid == Guid.Empty && ImageIndex == 0;

    public bool Equals(ProjectImageMoniker other) =>
        ImageListGuid == other.ImageListGuid && ImageIndex == other.ImageIndex;

    public override bool Equals(object? obj) => obj is ProjectImageMoniker m && Equals(m);

    public override int GetHashCode() => HashCode.Combine(ImageListGuid, ImageIndex);

    public static bool operator ==(ProjectImageMoniker left, ProjectImageMoniker right) => left.Equals(right);
    public static bool operator !=(ProjectImageMoniker left, ProjectImageMoniker right) => !left.Equals(right);

    public override string ToString() => $"{ImageListGuid}:{ImageIndex}";
}
