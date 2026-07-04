// Extracted marker interface: the real SolutionFolderNode (WinForms ExtTreeView Solution Explorer node) is
// out of MVP scope, but WriteableProjectConditionEvaluator.cs does an `is ISolutionFolderNode` check that
// needs the type to exist. No implementer exists in this MVP build, so the check is always false - the same
// runtime behavior "no Solution Explorer" produces anyway.
namespace ICSharpCode.SharpDevelop.Project
{
	public interface ISolutionFolderNode
	{
		ISolution Solution { get; }
	}
}
