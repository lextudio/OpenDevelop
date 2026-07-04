// HashSet<T>.AddRange is not in .NET's BCL — this shim adds it for linked project-system files.

using System.Collections.Generic;

namespace Microsoft.VisualStudio.ProjectSystem;

internal static class HashSetExtensions
{
    public static void AddRange<T>(this HashSet<T> set, IEnumerable<T> items)
    {
        foreach (var item in items)
            set.Add(item);
    }
}
