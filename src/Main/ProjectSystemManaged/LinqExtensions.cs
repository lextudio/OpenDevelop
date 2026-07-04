// CPS's own allocation-avoiding FirstOrDefault overload: passes an extra argument to the
// predicate instead of capturing it in a closure. Used by DependenciesSnapshotProvider.
// See docs/project-system.md (Slice 42).

namespace Microsoft.VisualStudio.ProjectSystem;

public static class LinqExtensions
{
    public static TSource? FirstOrDefault<TSource, TArg>(this IEnumerable<TSource> source, Func<TSource, TArg, bool> predicate, TArg arg)
    {
        foreach (var item in source)
        {
            if (predicate(item, arg))
            {
                return item;
            }
        }

        return default;
    }
}
