using ICSharpCode.Core;

namespace ICSharpCode.ILSpyAddIn.Commands
{
	/// <summary>
	/// Autostart no-op whose only purpose is forcing ILSpyAddIn.dll to load into the process at
	/// OpenDevelop startup. Without this, the assembly stays unloaded until some ILSpy-specific
	/// codon (menu item, display binding, ...) is actually built/invoked - but DevFlow's action
	/// discovery (LeXtudio.DevFlow.Agent.Core.DevFlowAgentServiceBase.DiscoverActions) only finds
	/// [DevFlowAction] methods in assemblies already present in AppDomain.CurrentDomain.
	/// GetAssemblies(), so od.ilspy.* actions (IlSpyDevFlowActions.cs) would 404 until something
	/// else happened to load this addin first.
	/// </summary>
	public sealed class RegisterDevFlowActionsCommand : AbstractCommand
	{
		public override void Run()
		{
		}
	}
}
