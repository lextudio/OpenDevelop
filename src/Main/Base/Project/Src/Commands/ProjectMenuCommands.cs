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
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project.Dialogs;
using Microsoft.Win32;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Project.Commands
{
	public class ViewProjectOptions : AbstractMenuCommand
	{
		public override void Run()
		{
			ShowProjectOptions(ProjectService.CurrentProject);
		}
		
		public static void ShowProjectOptions(IProject project)
		{
			if (project == null) {
				return;
			}
			foreach (IViewContent viewContent in SD.Workbench.ViewContentCollection) {
				ProjectOptionsView projectOptions = viewContent as ProjectOptionsView;
				if (projectOptions != null && projectOptions.Project == project) {
					projectOptions.WorkbenchWindow.SelectWindow();
					return;
				}
			}
			try {
				AddInTreeNode projectOptionsNode = AddInTree.GetTreeNode("/SharpDevelop/BackendBindings/ProjectOptions/" + project.Language);
				ProjectOptionsView projectOptions = new ProjectOptionsView(projectOptionsNode, project);
				SD.Workbench.ShowView(projectOptions);
			} catch (TreePathNotFoundException) {
				MessageService.ShowError("${res:Dialog.ProjectOptions.NoPanelsInstalledForProject}");
			}
		}
	}
	
	// Note: GenerateProjectDocumentation (Sandcastle Help File Builder integration - registry lookup +
	// ToolNotFoundDialog, both Win32/WinForms-only with no cross-platform meaning) was removed here.

	/// <summary>
	/// Opens the projects output folder in an explorer window.
	/// </summary>
	public class OpenProjectFolder : AbstractMenuCommand
	{
		public override void Run()
		{
			IProject project = ProjectService.CurrentProject;
			if (project == null) {
				return;
			}
			
			if (project.Directory != null && Directory.Exists(project.Directory))
				Process.Start("explorer.exe", "\"" + project.Directory + "\"");
		}
	}
	
	/// <summary>
	/// Opens the projects output folder in an explorer window.
	/// </summary>
	public class OpenProjectOutputFolder : AbstractMenuCommand
	{
		public override void Run()
		{
			CompilableProject project = ProjectService.CurrentProject as CompilableProject;
			if (project == null) {
				return;
			}
			
			// Explorer does not handle relative paths as a command line argument properly
			string outputFolder =  project.OutputFullPath;
			if (!Directory.Exists(outputFolder)) {
				Directory.CreateDirectory(outputFolder);
			}
			
			if (Directory.Exists(outputFolder))
				Process.Start("explorer.exe", "\"" + outputFolder + "\"");
		}
	}
}
