namespace Microsoft.VisualStudio.ProjectSystem;

internal static class ImmutableStringHashSet
{
    public static ImmutableHashSet<string> EmptyOrdinal { get; } =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    public static ImmutableHashSet<string> EmptyOrdinalIgnoreCase { get; } =
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
}
