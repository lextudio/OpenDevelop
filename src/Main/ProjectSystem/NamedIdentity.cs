using System;

namespace Microsoft.VisualStudio.ProjectSystem;

public sealed class NamedIdentity : IEquatable<NamedIdentity>
{
    public NamedIdentity(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public bool Equals(NamedIdentity? other)
    {
        return other is not null && StringComparer.Ordinal.Equals(Name, other.Name);
    }

    public override bool Equals(object? obj)
    {
        return obj is NamedIdentity other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Name);
    }

    public override string ToString()
    {
        return Name;
    }
}
