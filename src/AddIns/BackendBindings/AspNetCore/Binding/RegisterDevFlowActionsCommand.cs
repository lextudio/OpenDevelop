using ICSharpCode.Core;

namespace ICSharpCode.AspNetCore.AddIn
{
	/// <summary>
	/// Forces this addin assembly to load before DevFlow performs its one-time action scan.
	/// The command is a no-op; production behavior remains registered through the normal,
	/// ASP.NET Core-conditioned AddInTree entries.
	/// </summary>
	public sealed class RegisterDevFlowActionsCommand : AbstractCommand
	{
		public override void Run()
		{
		}
	}
}
