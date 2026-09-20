using System;

namespace ICSharpCode.Core
{
	/// <summary>
	/// A tiny decoupled channel between the startup work and any UI that wants to show it. The
	/// addin tree (deep in Core) has no reference to the SharpDevelop shell, so it just reports
	/// here - OpenDevelop's startup splash subscribes and renders it. Reporting is best-effort:
	/// no subscriber (or a broken one) must never affect startup.
	/// </summary>
	public static class StartupProgress
	{
		public static event Action<string> Reported;

		public static void Report(string message)
		{
			try {
				Reported?.Invoke(message);
			} catch {
				// Startup progress must never break startup.
			}
		}
	}
}
