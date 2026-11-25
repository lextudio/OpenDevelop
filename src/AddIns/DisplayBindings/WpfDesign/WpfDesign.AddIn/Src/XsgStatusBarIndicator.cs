using System;
using System.Linq;
using System.Threading;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Says in the status bar that the XAML file in front is compiled by the LibreWPF XAML source generator, and offers to open the C# it wrote. A
	/// project that compiles XAML to BAML gets nothing, so the message never replaces another one for the project kind that has no such feature.
	/// </summary>
	public sealed class XsgStatusBarIndicator : AbstractCommand
	{
		public override void Run()
		{
			MessageBus<WorkbenchContextChangedEventArgs>.Subscribers += OnActiveViewContentChanged;
		}

		// The file the pane was last decided for, and the message we set there. The workbench publishes a context change for a pad
		// activation as well, where the active document stays the same: nothing is evaluated or set again then, so another subsystem's message is
		// not overwritten and the project is not evaluated on every click.
		static string lastFile;
		static string ourMessage;

		static void OnActiveViewContentChanged(object sender, EventArgs e)
		{
			try {
				var file = SD.Workbench?.ActiveViewContent?.PrimaryFile;
				var xamlFile = file?.FileName.ToString();
				if (string.Equals(xamlFile, lastFile, StringComparison.OrdinalIgnoreCase))
					return;
				lastFile = xamlFile;
				var text = file == null ? null : Describe(file.FileName);
				if (text != null) {
					SD.StatusBar.SetMessage(text, actionText: "View Generated Code",
						action: () => _ = ViewGeneratedCodeAsync(xamlFile));
					ourMessage = text;
				} else if (ourMessage != null) {
					// Moved to a document the message is not about (a BAML project's XAML, a source file): take it down, once, and only while it is still
					// ours: another subsystem may have put a message there since (a null CurrentMessage means the service does not tell, so leave it).
					if (SD.StatusBar.CurrentMessage == ourMessage)
						SD.StatusBar.SetMessage(string.Empty);
					ourMessage = null;
				}
			} catch (Exception ex) when (ex is not OutOfMemoryException) {
				LoggingService.Warn("XSG status bar indicator failed: " + ex.Message);
			}
		}

		static async System.Threading.Tasks.Task ViewGeneratedCodeAsync(string xamlFile)
		{
			// Fire-and-forget from a button click: a failure (host unavailable, file I/O) has to reach the user here, as it does from the menu command.
			try {
				var (path, message) = await Commands.ViewGeneratedCode.OpenAsync(xamlFile, CancellationToken.None);
				if (path == null)
					SD.MessageService.ShowMessage(message);
			} catch (Exception ex) when (ex is not OutOfMemoryException) {
				LoggingService.Warn("View Generated Code failed: " + ex);
				SD.MessageService.ShowException(ex);
			}
		}

		/// <summary>The status text for a XAML file of a project compiled with the XAML source generator, otherwise null.</summary>
		public static string Describe(FileName fileName)
		{
			if (!string.Equals(System.IO.Path.GetExtension(fileName.ToString()), ".xaml", StringComparison.OrdinalIgnoreCase))
				return null;
			if (SD.ProjectService.FindProjectContainingFile(fileName) is not MSBuildBasedProject project)
				return null;
			var (xsg, simplerXaml, cSharpExpressions) = XsgProjectFeatures.Describe(project.GetEvaluatedProperty);
			return StatusText(xsg, simplerXaml, cSharpExpressions);
		}

		/// <summary>The status text for a project's XSG features; null for a project that does not compile XAML with the generator.</summary>
		public static string StatusText(bool xsg, bool simplerXaml, bool cSharpExpressions)
		{
			if (!xsg)
				return null;
			var features = new[] { simplerXaml ? "Simpler XAML" : null, cSharpExpressions ? "C# expressions" : null }.Where(f => f != null).ToArray();
			return "XAML compiled by the LibreWPF source generator" + (features.Length > 0 ? " (" + string.Join(", ", features) + ")" : string.Empty);
		}
	}
}
