// Replaces the excluded WinForms Form-based SplashScreen.cs. Keeps the pure command-line-parsing logic
// (SetCommandLineArgs/GetParameterList/GetRequestedFileList) that SharpDevelopMain.cs depends on; the splash
// screen UI itself is a no-op in this MVP build (ShowSplashScreen()/SplashScreen do nothing/return null).
using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Startup
{
	static class SplashScreenForm
	{
		static List<string> requestedFileList = new List<string>();
		static List<string> parameterList = new List<string>();

		public static object SplashScreen {
			get { return null; }
		}

		public static void ShowSplashScreen()
		{
			// no-op: WinForms splash screen UI is out of MVP scope.
		}

		public static string[] GetParameterList()
		{
			return parameterList.ToArray();
		}

		public static string[] GetRequestedFileList()
		{
			return requestedFileList.ToArray();
		}

		public static void SetCommandLineArgs(string[] args)
		{
			requestedFileList.Clear();
			parameterList.Clear();

			foreach (string arg in args) {
				if (arg.Length == 0) continue;
				if (arg[0] == '-' || arg[0] == '/') {
					int markerLength = 1;

					if (arg.Length >= 2 && arg[0] == '-' && arg[1] == '-') {
						markerLength = 2;
					}

					string param = arg.Substring(markerLength);
					if (param.EndsWith("\"", StringComparison.Ordinal))
						param = param.Substring(0, param.Length - 1) + "\\";
					parameterList.Add(param);
				} else {
					requestedFileList.Add(arg);
				}
			}
		}
	}
}
