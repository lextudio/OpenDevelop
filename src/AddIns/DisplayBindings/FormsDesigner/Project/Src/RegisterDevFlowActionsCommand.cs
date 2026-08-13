using ICSharpCode.Core;

namespace ICSharpCode.FormsDesigner
{
	/// <summary>
	/// Forces FormsDesigner.dll to load before DevFlow performs its one-time action discovery.
	/// The designer itself is normally loaded lazily when a designable source file is opened,
	/// which is too late for the FormsDesigner integration-test actions to be discovered.
	/// </summary>
	public sealed class RegisterDevFlowActionsCommand : AbstractCommand
	{
		public override void Run()
		{
		}
	}
}
