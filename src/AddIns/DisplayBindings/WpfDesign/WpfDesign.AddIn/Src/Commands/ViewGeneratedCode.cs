using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.WpfDesign.AddIn.Commands
{
	/// <summary>
	/// Opens the C# the XAML source generator wrote for the XAML file in the active designer or editor, read-only. Only projects that compile XAML
	/// with the generator have such code.
	/// </summary>
	public class ViewGeneratedCode : AbstractMenuCommand
	{
		public override async void Run()
		{
			try {
				var file = SD.Workbench.ActiveViewContent?.PrimaryFile;
				if (file == null)
					return;
				var result = await OpenAsync(file.FileName.ToString(), CancellationToken.None);
				if (result.Path == null)
					SD.MessageService.ShowMessage(result.Message);
			} catch (Exception ex) when (ex is not OutOfMemoryException) {
				SD.MessageService.ShowException(ex);
			}
		}

		/// <summary>Opens the generated code for <paramref name="xamlFile"/>; <c>Path</c> is the file opened, or null with a <c>Message</c> saying why not.</summary>
		public static async Task<(string Path, string Message)> OpenAsync(string xamlFile, CancellationToken cancellationToken)
		{
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(xamlFile)) as MSBuildBasedProject;
			if (project == null || !XsgProjectFeatures.Describe(project.GetEvaluatedProperty).Xsg)
				return (null, "This file's project does not compile XAML with the LibreWPF XAML source generator (UseLibreWpfXamlSourceGenerator).");
			var sources = await GeneratedSourceService.GetAsync(xamlFile, cancellationToken);
			if (sources.Count == 0)
				return (null, "The generator has produced no code for this file yet. Wait for the project to finish loading and try again.");
			var first = sources.First();
			var path = GeneratedSourceService.WriteReadOnlyCopy(project.Name, first);
			SD.FileService.OpenFile(FileName.Create(path));
			return (path, null);
		}
	}
}
