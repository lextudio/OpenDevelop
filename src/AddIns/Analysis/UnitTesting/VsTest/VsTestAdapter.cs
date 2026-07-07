using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.Core;

namespace ICSharpCode.UnitTesting
{
	abstract class VsTestAdapter
	{
		protected IDataSerializer dataSerializer = JsonDataSerializer.Instance;
		Process vsTestConsoleProcess;
		protected SocketCommunicationManager communicationManager;
		int clientConnectionTimeOut = 15000;
		Thread messageProcessingThread;
		CancellationTokenSource restartTokenSource = new CancellationTokenSource();

		protected static string GetRunSettings(IProject project)
		{
			return "<RunSettings>" +
				new RunConfiguration {
					DisableAppDomain = true,
					ResultsDirectory = Path.Combine(
						project.Directory ?? Environment.CurrentDirectory,
						"TestResults"),
					TestAdaptersPaths = GetTestAdapters(project)
				}.ToXml().OuterXml +
				"</RunSettings>";
		}

		static Dictionary<string, string> adaptersCache = new Dictionary<string, string>();

		protected static string GetTestAdapters(IProject project)
		{
			return string.Empty;
		}

		Task startTask;

		protected Task Start()
		{
			if (startTask == null)
				startTask = PrivateStart();
			return startTask;
		}

		protected virtual void OnStop()
		{
		}

		public void Stop()
		{
			OnStop();
			restartTokenSource.Cancel();

			try {
				if (communicationManager != null) {
					communicationManager.StopServer();
					communicationManager = null;
				}
			} catch (Exception ex) {
				SD.Log.Warn("TestPlatformCommunicationManager stop error.", ex);
			}

			try {
				if (vsTestConsoleProcess != null && !vsTestConsoleProcess.HasExited) {
					vsTestConsoleProcess.Kill();
					vsTestConsoleProcess = null;
				}
			} catch (Exception ex) {
				SD.Log.Warn("VSTest process dispose error.", ex);
			}
		}

		async Task PrivateStart()
		{
			var token = restartTokenSource.Token;
			startedSource = new TaskCompletionSource<bool>();
			communicationManager = new SocketCommunicationManager();
			var endPoint = communicationManager.HostServer(new IPEndPoint(IPAddress.Loopback, 0));
			communicationManager.AcceptClientAsync();
			vsTestConsoleProcess = StartVsTestConsoleExe(endPoint.Port);

			var sw = Stopwatch.StartNew();
			if (!await Task.Run(() => {
				while (!token.IsCancellationRequested) {
					if (communicationManager.WaitForClientConnection(100))
						return true;
					if (clientConnectionTimeOut < sw.ElapsedMilliseconds)
						return false;
				}
				return false;
			})) {
				sw.Stop();
				throw new TimeoutException("vstest.console failed to connect.");
			}
			sw.Stop();

			if (token.IsCancellationRequested)
				return;

			messageProcessingThread =
				new Thread(ReceiveMessages) {
					IsBackground = true
				};
			messageProcessingThread.Start(token);

			var timeoutDelay = Task.Delay(clientConnectionTimeOut);
			if (await Task.WhenAny(startedSource.Task, timeoutDelay) == timeoutDelay)
				throw new TimeoutException("vstest.console failed to respond.");
		}

		Process StartVsTestConsoleExe(int port)
		{
			var location = System.Reflection.Assembly.GetExecutingAssembly().Location;
			string vsTestConsoleExeFolder = Path.Combine(
				Path.GetDirectoryName(location),
				"VsTestConsole");
			string vsTestConsoleExe = Path.Combine(vsTestConsoleExeFolder, "vstest.console.exe");

			if (!File.Exists(vsTestConsoleExe))
				SD.Log.Warn("vstest.console.exe not found: " + vsTestConsoleExe);

			var psi = new ProcessStartInfo(vsTestConsoleExe) {
				Arguments = $"/parentprocessid:{Process.GetCurrentProcess().Id} /port:{port}",
				WorkingDirectory = vsTestConsoleExeFolder,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			var process = Process.Start(psi);
			return process;
		}

		void ReceiveMessages(object obj)
		{
			var token = (CancellationToken)obj;
			while (!token.IsCancellationRequested) {
				try {
					Message message = communicationManager.ReceiveMessage();
					ProcessMessage(message);
				} catch (IOException) {
				} catch (Exception ex) {
					SD.Log.Warn("TestPlatformAdapter receive message error.", ex);
				}
			}
		}

		protected void SendExtensionList(string[] extensions)
		{
			communicationManager.SendMessage(MessageType.ExtensionsInitialize, extensions);
		}

		protected virtual void ProcessMessage(Message message)
		{
			switch (message.MessageType) {
				case MessageType.SessionConnected:
					OnSessionConnected();
					break;
				default:
					SD.Log.Warn($"Unprocessed vstest message {message.MessageType}");
					break;
			}
		}

		TaskCompletionSource<bool> startedSource;

		void OnSessionConnected()
		{
			startedSource?.SetResult(true);
		}

		protected void SetStartedSource(TaskCompletionSource<bool> source)
		{
			startedSource = source;
		}

		protected static string GetAssemblyFileName(IProject project)
		{
			return project.OutputAssemblyFullPath;
		}
	}
}
