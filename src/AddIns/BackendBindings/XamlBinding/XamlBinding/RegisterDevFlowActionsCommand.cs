using ICSharpCode.Core;

namespace ICSharpCode.XamlBinding
{
	/// <summary>
	/// Autostart no-op whose only purpose is forcing ICSharpCode.XamlBinding.dll to load into the
	/// process at OpenDevelop startup. Without this, the assembly stays unloaded until some
	/// XamlBinding-specific codon (LanguageBinding, TextEditorExtension, ...) is actually built -
	/// but DevFlow's action discovery (LeXtudio.DevFlow.Agent.Core.DevFlowAgentServiceBase.DiscoverActions)
	/// only finds [DevFlowAction] methods in assemblies already present in
	/// AppDomain.CurrentDomain.GetAssemblies(), so od.xaml-outline.* actions
	/// (XamlOutlineDevFlowActions.cs) would 404 until something else happened to load this addin
	/// first. Same pattern as ILSpyAddIn's RegisterDevFlowActionsCommand.
	/// </summary>
	public sealed class RegisterDevFlowActionsCommand : AbstractCommand
	{
		public override void Run()
		{
		}
	}
}
