// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Windows;
using LeXtudio.DevFlow.Agent.Core;
using LeXtudio.DevFlow.Agent.Wpf;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.SharpDevelop.Startup
{
	/// <summary>
	/// Interaction logic for App.xaml
	/// </summary>
	partial class App : Application
	{
		public App()
		{
			InitializeComponent();
			if (IsDevFlowEnabled())
			{
				this.AddWpfDevFlowAgent(new AgentOptions { Port = GetAgentPort() });
			}
			// Log the exception instead of dying silently: an unhandled dispatcher exception
			// otherwise exits the process without a trace in the captured stdout/stderr (measured
			// during integration-test debugging), making intermittent startup crashes impossible
			// to diagnose. Then show the copyable exception dialog and let the user decide whether
			// to keep working - a single misbehaving command (e.g. a designer surface action that
			// creates a control off its owning thread) would otherwise take down the whole session.
			this.DispatcherUnhandledException += (_, e) =>
			{
				ICSharpCode.Core.LoggingService.Fatal("Unhandled dispatcher exception.", e.Exception);
				e.Handled = ICSharpCode.SharpDevelop.Logging.ExceptionBox.ShowErrorBox(
					e.Exception, "Unhandled exception", allowContinue: true);
			};

			// An exception thrown on a bare ThreadPool thread (e.g. inside Task.Run, when nothing
			// downstream observes the faulted Task) never reaches the Dispatcher at all - it goes
			// straight to the CLR's own unhandled-exception handling, which previously meant a
			// non-copyable native crash dialog with no trace of what actually happened. Log and
			// show the same copyable dialog here too; IsTerminating is normally true by this point
			// so the process still exits afterward, but at least the exception is visible and
			// selectable first.
			AppDomain.CurrentDomain.UnhandledException += (_, e) =>
			{
				if (e.ExceptionObject is Exception ex)
				{
					ICSharpCode.Core.LoggingService.Fatal("Unhandled AppDomain exception.", ex);
					ICSharpCode.SharpDevelop.Logging.ExceptionBox.ShowErrorBox(ex, "Unhandled exception");
				}
			};
		}

		/// <summary>
		/// Starts DevFlow in Debug builds, and in non-Debug builds only for an explicit
		/// integration-test launch. This keeps the unauthenticated local test endpoint out of
		/// ordinary release-app sessions while allowing the installed app to be the test target.
		/// DEVFLOW_ENABLE=1 is also available for an intentional diagnostic launch.
		/// </summary>
		static bool IsDevFlowEnabled()
		{
			if (Environment.GetEnvironmentVariable("DEVFLOW_DISABLE") == "1" || IsDevFlowDisabledByCommandLine())
				return false;
			if (GetDevFlowPortFromCommandLine().HasValue)
				return true;
			if (Environment.GetEnvironmentVariable("DEVFLOW_ENABLE") == "1")
				return true;
#if DEBUG
			return true;
#else
			return TestMode.IsActive;
#endif
		}

		/// <summary>
		/// True when the process was started with <c>-devflow:off</c> (or <c>/devflow:off</c>).
		///
		/// A second IDE instance launched to test an in-development addin cannot bind the same
		/// agent port as the parent that launched it, and the launcher has no way to set an
		/// environment variable: the project's StartArguments are the only channel it controls,
		/// and the debug path builds its own ProcessStartInfo and drops the caller's Environment
		/// dictionary entirely. So this reads the raw command line rather than
		/// SplashScreenForm's parsed list, which is not guaranteed to be populated before the
		/// Application constructor runs.
		/// </summary>
		static bool IsDevFlowDisabledByCommandLine()
		{
			foreach (string arg in Environment.GetCommandLineArgs()) {
				if (arg.Length < 2 || (arg[0] != '-' && arg[0] != '/'))
					continue;
				string parameter = arg.TrimStart('-', '/');
				if (string.Equals(parameter, "devflow:off", StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		/// <summary>
		/// DevFlow agent port: the DEVFLOW_AGENT_PORT environment variable wins (mirrors the
		/// LibreWpfDevFlowTestApp pattern), falling back to the pinned assembly metadata
		/// (DevFlowPort.cs), then the agent default. Lets concurrent IDE sessions run side by
		/// side without touching source.
		/// </summary>
		static int GetAgentPort()
		{
			var commandLinePort = GetDevFlowPortFromCommandLine();
			if (commandLinePort.HasValue)
				return commandLinePort.Value;
			var portValue = Environment.GetEnvironmentVariable("DEVFLOW_AGENT_PORT");
			if (int.TryParse(portValue, out var parsedPort) && parsedPort > 0)
				return parsedPort;
			return DevFlowAgentPortResolver.GetPortFromAssemblyMetadata() ?? AgentOptions.DefaultPort;
		}

		/// <summary>
		/// Reads <c>-devflow:&lt;port&gt;</c> without depending on command-line parsing being ready
		/// during the Application constructor. This lets an Addin SDK debug child expose a separate
		/// test endpoint while its parent keeps the normal DevFlow port.
		/// </summary>
		static int? GetDevFlowPortFromCommandLine()
		{
			foreach (string arg in Environment.GetCommandLineArgs()) {
				if (arg.Length < 2 || (arg[0] != '-' && arg[0] != '/'))
					continue;
				string parameter = arg.TrimStart('-', '/');
				const string prefix = "devflow:";
				if (!parameter.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					continue;
				var value = parameter.Substring(prefix.Length);
				if (int.TryParse(value, out var port) && port > 0)
					return port;
			}
			return null;
		}
	}
}
