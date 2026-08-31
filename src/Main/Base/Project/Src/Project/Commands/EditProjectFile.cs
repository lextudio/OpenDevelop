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
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project.Commands
{
	/// <summary>
	/// Opens the selected project's own project file in the text editor, the way Visual Studio's
	/// "Edit &lt;project&gt;.csproj" does.
	/// </summary>
	/// <remarks>
	/// Deliberately opens the file directly rather than going through "Open With": SDK-style projects
	/// are routinely hand-edited (target framework, package references), and making that a two-dialog
	/// detour is the difference between a normal edit and a chore. Saving the file re-applies it to
	/// the loaded project - see ProjectChangeWatcher.
	///
	/// Lives here (not under Src/Gui/Pads/ProjectBrowser/Commands) because that whole folder is
	/// excluded from this MVP build (legacy WinForms ExtTreeView-based Solution Explorer, out of
	/// scope) - unlike its former neighbors there, this command has no dependency on that legacy
	/// pad's types, so it can be compiled and still resolved by the
	/// /SharpDevelop/Pads/ProjectBrowser/ContextMenu/ProjectNode AddInTree path the modern
	/// ProjectBrowserControl also uses for its right-click menu.
	/// </remarks>
	public class EditProjectFile : AbstractMenuCommand
	{
		public override void Run()
		{
			var project = ProjectService.CurrentProject;
			if (project == null)
				return;
			var fileName = project.FileName;
			if (fileName == null || !SD.FileSystem.FileExists(fileName))
				return;
			SD.FileService.OpenFile(fileName);
		}
	}
}
