#if HAS_UNO
using System;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui
{
	public static class WorkbenchSingleton
	{
		static IWorkbench? workbench;

		public static IWorkbench? Workbench => workbench;
		public static object? MainWindow => null;

		public static event EventHandler? WorkbenchCreated;

		static volatile bool workbenchActive;

		public static bool WorkbenchActive => workbenchActive;

		public static void SetWorkbench(IWorkbench workbenchInstance)
		{
			workbench = workbenchInstance ?? throw new ArgumentNullException(nameof(workbenchInstance));
			workbenchActive = true;
			WorkbenchCreated?.Invoke(null, EventArgs.Empty);
		}

		public static void AssertWorkbenchCreated()
		{
		}

		public static void DispatchAsync(Action action)
		{
			Task.Run(action);
		}
	}
}
#endif
