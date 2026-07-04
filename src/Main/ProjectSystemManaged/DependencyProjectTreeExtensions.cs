// Extension methods that need both IProjectTree (shim) and IDependency (Managed).
// Mirrors IProjectTreeExtensions.FindChildForDependency from project-system.

using System;
using System.Linq;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies;

namespace Microsoft.VisualStudio.ProjectSystem;

internal static class DependencyProjectTreeExtensions
{
    /// <summary>
    /// Returns the first direct child that represents <paramref name="dependency"/> (by id or caption), or null.
    /// </summary>
    public static IProjectTree? FindChildForDependency(this IProjectTree tree, IDependency dependency) =>
        tree.Children.FirstOrDefault(
            child =>
                string.Equals(dependency.Id, child.BrowseObjectProperties?.ItemName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dependency.Caption, child.Caption, StringComparison.OrdinalIgnoreCase));
}
