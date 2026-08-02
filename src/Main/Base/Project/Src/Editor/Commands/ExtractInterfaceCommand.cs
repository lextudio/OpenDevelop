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

// Goes through the shared ILanguageService contract (doc/technotes/csharp-vb-binding.md) rather
// than RoslynWorkspaceHelper/Microsoft.CodeAnalysis directly. The pre-2011 NRefactory-era
// ExtractInterfaceOptions/RefactoringProvider engine this used to call was never revived across two
// separate rewrites (upstream's own 2011 NRefactory-version migration, then this project's Roslyn
// migration) - see doc/technotes/csharp-roslyn.md.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor.Dialogs;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Roslyn;

namespace ICSharpCode.SharpDevelop.Editor.Commands
{
	/// <summary>
	/// Extracts a new interface from the public members of the class at the caret.
	/// </summary>
	public class ExtractInterfaceCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var editor = SD.GetActiveViewContentService<ITextEditor>();
			if (editor == null || editor.FileName == null)
				return;
			_ = RunAsync(editor);
		}

		async Task RunAsync(ITextEditor editor)
		{
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(editor.FileName, out var service))
				return;

			string fileName = editor.FileName.ToString();
			var id = new DocumentId(fileName);
			int offset = editor.Caret.Offset;

			ExtractInterfaceInfo info;
			try {
				await service.UpsertDocumentAsync(id, editor.Document.Text, CancellationToken.None);
				info = await service.GetExtractInterfaceInfoAsync(id, offset, CancellationToken.None);
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error resolving type for Extract Interface.");
				return;
			}

			if (info == null) {
				SD.MessageService.ShowMessage(
					StringParser.Parse("${res:SharpDevelop.Refactoring.ExtractInterfaceCommand}") + ": place the caret on a class name.");
				return;
			}
			if (info.Members.Count == 0) {
				SD.MessageService.ShowMessage("This class has no public instance members to extract.");
				return;
			}

			var classFileDirectory = Path.GetDirectoryName(fileName)!;
			var suggestedInterfaceName = "I" + info.TypeName;
			var dialog = new ExtractInterfaceDialog {
				Owner = SD.Workbench.MainWindow,
				InterfaceName = suggestedInterfaceName,
				NewFileName = Path.Combine(classFileDirectory, suggestedInterfaceName + ".cs"),
			};
			foreach (var member in info.Members)
				dialog.Members.Add(new ExtractInterfaceDialog.MemberOption(member));

			if (dialog.ShowDialog() != true)
				return;

			try {
				var result = await service.ExtractInterfaceAsync(
					id, offset, dialog.InterfaceName, dialog.ChosenMemberIds, dialog.AddInterfaceToClass, dialog.IncludeComments,
					CancellationToken.None);
				if (result == null)
					return;

				File.WriteAllText(dialog.NewFileName, result.InterfaceFileContent);
				RoslynWorkspaceHelper.OpenAndReplaceText(dialog.NewFileName, result.InterfaceFileContent);
				AddCompileItemIfNonSdkProject(fileName, dialog.NewFileName);

				foreach (var pair in result.Edits)
					ApplyEdits(pair.Key, pair.Value);
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error extracting interface.");
			}
		}

		/// <summary>
		/// SDK-style projects (the common case) pick up a new file under the project directory
		/// automatically via their own implicit glob. Legacy (non-SDK) projects have no such glob:
		/// a new file only becomes part of the project - and therefore only shows up in Solution
		/// Explorer or gets compiled - if it's explicitly added as a Compile item, so do that here.
		/// This is project-system plumbing, not language-service data, so it stays caller-side.
		/// </summary>
		static void AddCompileItemIfNonSdkProject(string classFilePath, string newFilePath)
		{
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(classFilePath));
			if (project is not Project.MSBuildBasedProject msbuildProject || msbuildProject.IsSdkStyleProject)
				return;

			var relativeInclude = FileUtility.GetRelativePath(project.Directory.ToString(), newFilePath);
			project.Items.Add(new Project.FileProjectItem(project, Project.ItemType.Compile, relativeInclude));
			project.Save();
		}

		static void ApplyEdits(string fileName, System.Collections.Generic.IReadOnlyList<TextEdit> edits)
		{
			if (edits.Count == 0 || !File.Exists(fileName))
				return;

			string text = File.ReadAllText(fileName);
			foreach (var edit in edits) {
				int start = FindReferencesCommand.GetOffset(text, edit.Span.Start.Line, edit.Span.Start.Column);
				int end = FindReferencesCommand.GetOffset(text, edit.Span.End.Line, edit.Span.End.Column);
				text = text.Substring(0, start) + edit.NewText + text.Substring(end);
			}

			RoslynWorkspaceHelper.OpenAndReplaceText(fileName, text);
		}
	}
}
