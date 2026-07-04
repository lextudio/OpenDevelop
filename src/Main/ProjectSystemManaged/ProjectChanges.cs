namespace Microsoft.VisualStudio.ProjectSystem;

public interface IProjectChangeDescription
{
    IProjectChangeSnapshot Before { get; }
    IProjectChangeSnapshot After { get; }
    IProjectChangeDiff Difference { get; }
}

public interface IProjectChangeSnapshot
{
    IImmutableDictionary<string, IImmutableDictionary<string, string>> Items { get; }
}

public interface IProjectChangeDiff
{
    IImmutableSet<string> AddedItems { get; }
    IImmutableSet<string> RemovedItems { get; }
    IImmutableSet<string> ChangedItems { get; }
    IImmutableDictionary<string, string> RenamedItems { get; }
    IImmutableSet<string> ChangedProperties { get; }
    bool AnyChanges { get; }
}

internal static class ProjectChangeSnapshotExtensions
{
    public static bool IsEvaluationSucceeded(this IProjectChangeSnapshot snapshot)
    {
        return true;
    }
}
