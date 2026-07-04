// Clean-room stub. Microsoft.VisualStudio.Imaging.Interop.ImageMoniker is a VS SDK type.
// We reproduce only the struct shape used by dotnet/project-system.
// See docs/project-system.md.

using System;

namespace Microsoft.VisualStudio.Imaging.Interop;

/// <summary>
/// Identifies an image in the VS image service (GUID + index).
/// Mirrors the VS SDK struct surface consumed by dotnet/project-system.
/// </summary>
public readonly struct ImageMoniker : IEquatable<ImageMoniker>
{
    public static readonly ImageMoniker Empty = default;

    public Guid Guid { get; }
    public int Id { get; }

    public ImageMoniker(Guid guid, int id)
    {
        Guid = guid;
        Id = id;
    }

    public bool Equals(ImageMoniker other) => Guid == other.Guid && Id == other.Id;
    public override bool Equals(object? obj) => obj is ImageMoniker m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(Guid, Id);
    public static bool operator ==(ImageMoniker left, ImageMoniker right) => left.Equals(right);
    public static bool operator !=(ImageMoniker left, ImageMoniker right) => !left.Equals(right);
    public override string ToString() => $"{Guid}:{Id}";
}
