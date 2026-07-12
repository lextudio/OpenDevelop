// MVP ProjectBrowser entry point. The old WinForms/ExtTreeView ProjectBrowser is out of scope;
// OpenDevelop's WPF ProjectBrowser implementation lives in the executable assembly, so Base uses
// a late-bound service lookup to keep the original ProjectBrowserPad refresh contract.
using System;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project
{
	public static class ProjectBrowserPad
	{
		public static void RefreshViewAsync()
		{
			Type controllerType = Type.GetType(
				"ICSharpCode.SharpDevelop.Services.IProjectBrowserController, OpenDevelop",
				throwOnError: false);
			if (controllerType == null) {
				return;
			}
			
			object controller = ServiceSingleton.ServiceProvider.GetService(controllerType);
			controllerType.GetMethod("Refresh")?.Invoke(controller, null);
		}
	}
}
