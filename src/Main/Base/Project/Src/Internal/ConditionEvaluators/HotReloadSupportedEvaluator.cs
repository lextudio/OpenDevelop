using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Project.HotReload;

namespace ICSharpCode.SharpDevelop;

/// <summary>Enables the Hot Reload command only for the currently supported launch adapter.</summary>
public sealed class HotReloadSupportedConditionEvaluator : IConditionEvaluator
{
	public bool IsValid(object caller, Condition condition)
	{
		var project = caller as IProject ?? ProjectService.OpenSolution?.StartupProject;
		return HotReloadService.IsSupported(project);
	}
}
