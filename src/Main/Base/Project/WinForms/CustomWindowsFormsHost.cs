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

using System.Windows.Forms.Integration;

namespace ICSharpCode.SharpDevelop.WinForms
{
	/// <summary>
	/// Common named base for every WindowsFormsHost SharpDevelop creates.
	/// </summary>
	/// <remarks>
	/// This class was referenced (as the return type of IWinFormsService.CreateWindowsFormsHost and the
	/// cast target in FormsDesigner's DesignerViewContent.UserContent) but never itself ported when this
	/// fork moved to .NET Core / LibreWinForms - only its one concrete subclass, SDWindowsFormsHost, ships
	/// in this assembly's Main/SharpDevelop project. Living in Main/Base/Project (alongside the
	/// IWinFormsService interface that names it) rather than next to SDWindowsFormsHost is what lets
	/// FormsDesigner reference the type without depending on the exe project. It adds nothing beyond the
	/// real WindowsFormsHost: every member SDWindowsFormsHost and its callers actually use (Child,
	/// Dispose(bool), CommandBindings) already comes from WindowsFormsHost/FrameworkElement.
	/// </remarks>
	public class CustomWindowsFormsHost : WindowsFormsHost
	{
	}
}
