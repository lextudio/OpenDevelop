using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace ICSharpCode.SharpDevelop.Startup
{
	/// <summary>
	/// The startup splash - the WPF rewrite of the original WinForms <c>SplashScreen.cs</c>
	/// (a <c>Form</c> with a bitmap), keeping the same type name so the port is obvious at a
	/// glance. It is shown as the application's FIRST window (LibreWPF only maps a
	/// window while the run loop is running - see SharpDevelopMain.RunApplication), while the
	/// host, addin tree and workbench are built behind it, and it renders the live progress
	/// reported through <see cref="ICSharpCode.Core.StartupProgress"/>.
	///
	/// It also owns the pure command-line parsing SharpDevelopMain depends on
	/// (SetCommandLineArgs/GetParameterList/GetRequestedFileList).
	/// </summary>
	partial class SplashScreenForm : Window
	{
		const int RecentLines = 4;
		readonly Queue<string> recent = new();

		public SplashScreenForm()
		{
			InitializeComponent();
			ICSharpCode.Core.StartupProgress.Reported += OnProgressReported;
			Closed += (_, _) => ICSharpCode.Core.StartupProgress.Reported -= OnProgressReported;
		}

		void OnProgressReported(string message)
		{
			// Reported while the addin tree loads, which may be a helper thread - never touch the
			// visual tree directly.
			Dispatcher.BeginInvoke(new Action(() => {
				Status.Text = message;
				recent.Enqueue(message);
				while (recent.Count > RecentLines)
					recent.Dequeue();
				Recent.Text = string.Join("\n", recent);
			}));
		}

		static readonly List<string> requestedFileList = new();
		static readonly List<string> parameterList = new();

		public static string[] GetParameterList()
		{
			return parameterList.ToArray();
		}

		public static string[] GetRequestedFileList()
		{
			return requestedFileList.ToArray();
		}

		/// <summary>True when -nologo (or /nologo) was passed, or the app is running under the
		/// integration-test harness (OD_TEST_MODE), which both suppress the startup splash.</summary>
		public static bool IsLogoSuppressed()
		{
			if (string.Equals(Environment.GetEnvironmentVariable("OD_TEST_MODE"), "1", StringComparison.Ordinal))
				return true;
			foreach (string parameter in parameterList)
				if (string.Equals(parameter, "nologo", StringComparison.OrdinalIgnoreCase))
					return true;
			return false;
		}

		public static void SetCommandLineArgs(string[] args)
		{
			requestedFileList.Clear();
			parameterList.Clear();

			foreach (string arg in args) {
				if (arg.Length == 0) continue;
				// A leading '/' marks a switch on Windows only: on Unix an absolute path starts
				// with '/' as well, so an argument that names an existing file or directory is a
				// requested file, not a switch. Without this, 'OpenDevelop /path/to/x.slnx'
				// silently started with nothing open - the path went to the parameter list and was
				// never handed to the workbench.
				if ((arg[0] == '-' || arg[0] == '/') && !File.Exists(arg) && !Directory.Exists(arg)) {
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
