namespace ICSharpCode.Core
{
	/// <summary>
	/// A no-op add-in-tree command used solely to load its containing add-in assembly before
	/// DevFlow discovers action attributes. Keep the concrete command type in each add-in: its
	/// manifest is the compatibility boundary; this base centralizes only the identical behavior.
	/// </summary>
	public abstract class DevFlowActionRegistrationCommand : AbstractCommand
	{
		public sealed override void Run()
		{
		}
	}
}
