namespace Microsoft.VisualStudio.ProjectSystem;

internal static class ImmutableStringDictionary<T>
{
    public static ImmutableDictionary<string, T> EmptyOrdinal { get; } =
        ImmutableDictionary.Create<string, T>(StringComparer.Ordinal);

    public static ImmutableDictionary<string, T> EmptyOrdinalIgnoreCase { get; } =
        ImmutableDictionary.Create<string, T>(StringComparer.OrdinalIgnoreCase);
}
