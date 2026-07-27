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

// Rewritten against Microsoft.CodeAnalysis directly (see RoslynWorkspaceHelper.ExtractInterfaceAsync).
// The pre-2011 NRefactory-era ExtractInterfaceOptions/RefactoringProvider engine this used to call
// was never revived across two separate rewrites (upstream's own 2011 NRefactory-version migration,
// then this project's Roslyn migration) - see doc/technotes/csharp-roslyn.md.

using System;
using System.IO;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor.Dialogs;
using ICSharpCode.SharpDevelop.Roslyn;
using Microsoft.CodeAnalysis;

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
			RunAsync(editor);
		}

		async void RunAsync(ITextEditor editor)
		{
			string fileName = editor.FileName.ToString();

			INamedTypeSymbol classSymbol;
			try {
				var document = RoslynWorkspaceHelper.FindDocument(fileName, editor.Document.Text);
				var symbol = document != null ? RoslynWorkspaceHelper.GetSymbolAt(document, editor.Caret.Location) : null;
				classSymbol = symbol as INamedTypeSymbol;
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error resolving type for Extract Interface.");
				return;
			}

			if (classSymbol == null || classSymbol.TypeKind != TypeKind.Class) {
				SD.MessageService.ShowMessage(
					StringParser.Parse("${res:SharpDevelop.Refactoring.ExtractInterfaceCommand}") + ": place the caret on a class name.");
				return;
			}

			var candidateMembers = RoslynWorkspaceHelper.GetExtractInterfaceCandidateMembers(classSymbol);
			if (candidateMembers.Count == 0) {
				SD.MessageService.ShowMessage("This class has no public instance members to extract.");
				return;
			}

			var classFileDirectory = Path.GetDirectoryName(fileName)!;
			var suggestedInterfaceName = "I" + classSymbol.Name;
			var dialog = new ExtractInterfaceDialog {
				Owner = SD.Workbench.MainWindow,
				InterfaceName = suggestedInterfaceName,
				NewFileName = Path.Combine(classFileDirectory, suggestedInterfaceName + ".cs"),
			};
			foreach (var member in candidateMembers)
				dialog.Members.Add(new ExtractInterfaceDialog.MemberOption(member));

			if (dialog.ShowDialog() != true)
				return;

			try {
				await RoslynWorkspaceHelper.ExtractInterfaceAsync(
					classSymbol, dialog.InterfaceName, dialog.ChosenMembers, dialog.AddInterfaceToClass, dialog.NewFileName,
					dialog.IncludeComments);
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error extracting interface.");
			}
		}
	}
}
