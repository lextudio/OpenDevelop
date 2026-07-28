namespace ICSharpCode.SearchAndReplace.Portable;

public sealed record PortableReplaceFilePlan(
	string FilePath,
	string OriginalText,
	string UpdatedText,
	int MatchCount)
{
	public bool HasChanges => !string.Equals(OriginalText, UpdatedText, System.StringComparison.Ordinal);
}
