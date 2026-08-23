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

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>
	/// Implement this interface to make your view content display tools in the tool box.
	/// </summary>
	/// <remarks>
	/// The pad that hosts this content (<c>ToolsPad</c>, AddInTree pad id "SideBar") moved to the
	/// App project as a thin shim over <c>ToolsPadViewModel</c> (doc/technotes/ilspy.md "Docking
	/// and layout replacement" item 4, 2026-08-03). This interface stays here since AddIns that
	/// implement it (WpfDesign, FormsDesigner, AvalonEdit.AddIn, Reporting, WorkflowDesigner,
	/// Data.EDMDesigner) reference the Base project, not the App project.
	/// </remarks>
	[ViewContentService]
	public interface IToolsHost
	{
		/// <summary>
		/// Gets the control to display in the tool box.
		/// </summary>
		object ToolsContent { get; }
	}

	/// <summary>
	/// Application-level host for the shared Tools pad. This is deliberately separate from
	/// <see cref="IToolsHost"/>, which describes the active document rather than the pad itself.
	/// </summary>
	public interface IToolsPadHost
	{
		/// <summary>The document-provided content currently assigned to the real Tools pad.</summary>
		object HostedContent { get; }
	}

	/// <summary>
	/// Marker service (registered into <c>SD.Services</c>, not a per-view <see cref="ViewContentServiceAttribute"/>)
	/// for the single shared WPF-hosted toolbox control that more than one AddIn's <see cref="IToolsHost.ToolsContent"/>
	/// can return - e.g. WpfDesign.AddIn's WpfToolbox, reused by FormsDesigner for WinForms controls so both the
	/// XAML and WinForms designers drag from the exact same palette instance. Lives here (Base project, which both
	/// AddIns already reference) purely so neither AddIn needs a compile-time reference to the other.
	/// </summary>
	public interface ISharedToolboxHost
	{
		/// <summary>Gets the shared toolbox's WPF control (a ListBox), the same object every caller gets back.</summary>
		object ToolboxControl { get; }
	}
}
