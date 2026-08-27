// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Autostart command that registers the Stride .sdpkg tree contribution. Declared on
// /SharpDevelop/Autostart (see ICSharpCode.StrideGameStudio.addin) so this addin's assembly loads
// at OpenDevelop startup and the contributor is in place before any solution tree is built -
// same pattern as XamlBinding/ILSpyAddIn's RegisterDevFlowActionsCommand.

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.StrideGameStudio
{
	/// <summary>
	/// Registers <see cref="StrideProjectTreeContributor"/> so Solution Explorer shows the
	/// .sdpkg-backed Assets subtree under every Stride game project.
	/// </summary>
	public sealed class RegisterStrideProjectTreeContributorCommand : AbstractCommand
	{
		public override void Run()
		{
			ProjectTreeContributorRegistry.Register(new StrideProjectTreeContributor());
		}
	}
}