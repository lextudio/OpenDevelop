// Clean-room stubs for CPS project configuration types.
// Reconstructed from dotnet/project-system MIT usage only.
// See docs/project-system.md.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>
/// Identifies a single configuration dimension slice (e.g. TargetFramework=net8.0).
/// </summary>
public sealed class ProjectConfigurationSlice : IEquatable<ProjectConfigurationSlice>
{
    public IImmutableDictionary<string, string> Dimensions { get; }

    public ProjectConfigurationSlice(IImmutableDictionary<string, string> dimensions)
    {
        Dimensions = dimensions;
    }

    public static ProjectConfigurationSlice Create(IImmutableDictionary<string, string> dimensions) =>
        new(dimensions);

    public bool Equals(ProjectConfigurationSlice? other) =>
        other is not null &&
        Dimensions.Count == other.Dimensions.Count &&
        !Dimensions.Keys.Except(other.Dimensions.Keys, StringComparer.OrdinalIgnoreCase).Any();

    public override bool Equals(object? obj) => Equals(obj as ProjectConfigurationSlice);

    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var kv in Dimensions)
            hash ^= HashCode.Combine(kv.Key, kv.Value);
        return hash;
    }

    public override string ToString() =>
        string.Join("|", Dimensions.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>
    /// Whether this slice's dimensions match the given active configuration — i.e. this is the
    /// slice that should be treated as "primary" for display purposes when a project has multiple
    /// configuration slices (e.g. multi-TFM).
    /// </summary>
    public bool IsPrimaryActiveSlice(ProjectConfiguration configuration) =>
        Dimensions.All(kv =>
            configuration.Dimensions.TryGetValue(kv.Key, out var value) &&
            string.Equals(value, kv.Value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Represents a configured project (one TFM/platform/configuration combination).
/// Minimal stub — enough to compile the dependency snapshot types.
/// </summary>
public abstract class ConfiguredProject
{
    public abstract UnconfiguredProject UnconfiguredProject { get; }
    public abstract ProjectConfiguration ProjectConfiguration { get; }
}

/// <summary>
/// Represents an unconfigured (multi-TFM) project.
/// Minimal stub.
/// </summary>
public abstract class UnconfiguredProject
{
    public abstract string FullPath { get; }
}

/// <summary>
/// Represents a named project configuration (Debug|AnyCPU).
/// </summary>
public sealed class ProjectConfiguration
{
    public string Name { get; }
    public IImmutableDictionary<string, string> Dimensions { get; }

    public ProjectConfiguration(string name, IImmutableDictionary<string, string> dimensions)
    {
        Name = name;
        Dimensions = dimensions;
    }
}
