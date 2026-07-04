// Clean-room reimplementation of the subset of MetadataExtensions used by
// dotnet/project-system's dependency snapshot types.
// GetProjectItemProperties is omitted (requires IProjectRuleSnapshot from CPS SDK).
// See docs/project-system.md.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.VisualStudio.ProjectSystem.Properties;

public static class MetadataExtensions
{
    public static bool TryGetStringProperty(
        this IImmutableDictionary<string, string> properties,
        string key,
        [NotNullWhen(true)] out string? value)
    {
        if (properties.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            return true;
        value = null;
        return false;
    }

    public static string? GetStringProperty(
        this IImmutableDictionary<string, string> properties,
        string key) =>
        properties.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public static bool TryGetBoolProperty(
        this IImmutableDictionary<string, string> properties,
        string key,
        out bool value)
    {
        if (properties.TryGetStringProperty(key, out var text) && bool.TryParse(text, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public static bool? GetBoolProperty(
        this IImmutableDictionary<string, string> properties,
        string key) =>
        properties.TryGetBoolProperty(key, out var value) ? value : null;
}
