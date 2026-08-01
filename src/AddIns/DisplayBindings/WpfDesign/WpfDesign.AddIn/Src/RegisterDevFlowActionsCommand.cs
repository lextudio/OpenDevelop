using ICSharpCode.Core;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Autostart no-op whose only purpose is forcing ICSharpCode.WpfDesign.AddIn.dll to load into
	/// the process at OpenDevelop startup. Without this, the assembly would only load once some
	/// WPF-designer-specific codon (display binding, ...) is actually built for an open document -
	/// but DevFlow's action discovery (LeXtudio.DevFlow.Agent.Core.DevFlowAgentServiceBase.DiscoverActions)
	/// only finds [DevFlowAction] methods in assemblies already present in
	/// AppDomain.CurrentDomain.GetAssemblies(), so od.wpf-designer.* actions
	/// (WpfDesignDevFlowActions.cs) could 404 until something else happened to load this addin
	/// first. Same pattern as ILSpyAddIn's RegisterDevFlowActionsCommand.
	/// </summary>
	public sealed class RegisterDevFlowActionsCommand : AbstractCommand
	{
		public override void Run()
		{
		}
	}
}
