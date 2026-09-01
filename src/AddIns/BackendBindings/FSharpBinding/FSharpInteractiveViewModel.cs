using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.Windows.Input;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.ILSpy.ViewModels;

namespace FSharpBinding
{
	/// <summary>
	/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-09) replacement for the
	/// legacy AddInTree-registered <see cref="FSharpInteractive"/> (AddInTree pad id
	/// "FSharpInteractive"). Not a MEF part - the AddIn's assembly is never scanned by
	/// <c>OpenDevelopMefHost</c> - so it is constructed with a plain <c>new</c> by the
	/// <see cref="FSharpInteractive"/> shim on first real use and registered with the real
	/// docking host via <c>IPaneModelHost.Add</c>. Hosts the shared <see cref="ConsolePadCore"/>
	/// (the same console body <see cref="AbstractConsolePad"/> wraps for its legacy consumers).
	/// </summary>
	sealed class FSharpInteractiveViewModel : ToolPaneModel
	{
		readonly ConsolePadCore core;
		readonly Queue<string> outputQueue = new Queue<string>();
		internal readonly Process fsiProcess = new Process();
		internal readonly bool foundCompiler;
		int expectedPrompts;

		public FSharpInteractiveViewModel()
		{
			Title = "F# Interactive";
			ContentId = "FSharpInteractive";
			IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
			IsCloseable = true;
			LegacyPadClass = typeof(FSharpInteractive).FullName;
			PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Bottom;

			core = new ConsolePadCore(() => "> ", AcceptCommand, null, BuildToolBar);
			Content = core.Content;

			if (Array.Exists(ConfigurationManager.AppSettings.AllKeys, x => x == "alt_fs_bin_path")) {
				string path = Path.Combine(ConfigurationManager.AppSettings["alt_fs_bin_path"], "fsi.exe");
				if (File.Exists(path)) {
					fsiProcess.StartInfo.FileName = path;
					foundCompiler = true;
				} else {
					core.AppendLine("you are trying to use the app setting alt_fs_bin_path, but fsi.exe is not localed in the given directory");
					foundCompiler = false;
				}
			} else {
				string[] paths = Environment.GetEnvironmentVariable("PATH").Split(';');
				string path = Array.Find(paths, x => {
				                         	try {
				                         		return File.Exists(Path.Combine(x, "fsi.exe"));
				                         	} catch {
				                         		return false;
				                         	}});
				if (path != null) {
					fsiProcess.StartInfo.FileName = Path.Combine(path, "fsi.exe");
					foundCompiler = true;
				} else {
					path = FindFSharpInteractiveInProgramFilesFolder();
					if (path != null) {
						fsiProcess.StartInfo.FileName = path;
						foundCompiler = true;
					} else {
						core.AppendLine("Can not find the fsi.exe, ensure a version of the F# compiler is installed." + Environment.NewLine +
						           "Please see http://research.microsoft.com/fsharp for details of how to install the compiler");
						foundCompiler = false;
					}
				}
			}

			if (foundCompiler) {
				//fsiProcess.StartInfo.Arguments <- "--fsi-server sharpdevelopfsi";
				fsiProcess.StartInfo.UseShellExecute = false;
				fsiProcess.StartInfo.CreateNoWindow = true;
				fsiProcess.StartInfo.RedirectStandardError = true;
				fsiProcess.StartInfo.RedirectStandardInput = true;
				fsiProcess.StartInfo.RedirectStandardOutput = true;
				fsiProcess.EnableRaisingEvents = true;
				fsiProcess.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
					lock (outputQueue) {
						outputQueue.Enqueue(e.Data);
					}
					SD.MainThread.InvokeAsyncAndForget(ReadAll);
				};
				fsiProcess.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
					lock (outputQueue) {
						outputQueue.Enqueue(e.Data);
					}
					SD.MainThread.InvokeAsyncAndForget(ReadAll);
				};
				fsiProcess.Exited += delegate(object sender, EventArgs e) {
					lock (outputQueue) {
						outputQueue.Enqueue("fsi.exe died");
						outputQueue.Enqueue("restarting ...");
					}
					SD.MainThread.InvokeAsyncAndForget(ReadAll);
					SD.MainThread.InvokeAsyncAndForget(StartFSharp);
				};
				StartFSharp();
			}
		}

		ToolBar BuildToolBar(ConsoleControl console)
		{
			return ToolBarService.CreateToolBar(console, this, "/SharpDevelop/Pads/CommonConsole/ToolBar");
		}

		string FindFSharpInteractiveInProgramFilesFolder()
		{
			var fileNames = new string [] {
				@"Microsoft SDKs\F#\3.1\Framework\v4.0\Fsi.exe",
				@"Microsoft SDKs\F#\3.0\Framework\v4.0\Fsi.exe",
				@"Microsoft F#\v4.0\Fsi.exe"
			};
			return FindFirstMatchingFileInProgramFiles(fileNames);
		}

		string FindFirstMatchingFileInProgramFiles(string[] fileNames)
		{
			string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			return fileNames.Select(fileName => Path.Combine(programFiles, fileName))
				.FirstOrDefault(fullPath => File.Exists(fullPath));
		}

		void StartFSharp()
		{
			fsiProcess.Start();
			fsiProcess.BeginErrorReadLine();
			fsiProcess.BeginOutputReadLine();
		}

		void ReadAll()
		{
			StringBuilder b = new StringBuilder();
			lock (outputQueue) {
				while (outputQueue.Count > 0)
					b.AppendLine(outputQueue.Dequeue());
			}
			int offset = 0;
			// ignore prompts inserted by fsi.exe (we only see them too late as we're reading line per line)
			for (int i = 0; i < expectedPrompts; i++) {
				if (offset + 1 < b.Length && b[offset] == '>' && b[offset + 1] == ' ')
					offset += 2;
				else
					break;
			}
			expectedPrompts = 0;
			core.InsertBeforePrompt(b.ToString(offset, b.Length - offset));
		}

		bool AcceptCommand(string command)
		{
			if (command.TrimEnd().EndsWith(";;", StringComparison.Ordinal)) {
				expectedPrompts++;
				fsiProcess.StandardInput.WriteLine(command);
				return true;
			}
			return false;
		}
	}
}
