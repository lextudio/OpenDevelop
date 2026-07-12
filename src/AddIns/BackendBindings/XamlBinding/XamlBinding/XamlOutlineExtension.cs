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

using System.ComponentModel.Design;

using ICSharpCode.AvalonEdit;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.XamlBinding
{
	/// <summary>
	/// Attaches <see cref="XamlOutlineContentHost"/> (an <see cref="IOutlineContentHost"/> backed
	/// by the XAML language server's textDocument/documentSymbol) to every .xaml text editor view,
	/// so the Outline pad shows the file's element tree without any local XAML parsing.
	/// </summary>
	public class XamlOutlineExtension : ITextEditorExtension
	{
		TextEditor textEditor;
		IServiceContainer services;
		XamlOutlineContentHost host;

		public void Attach(ITextEditor editor)
		{
			textEditor = editor.GetService<TextEditor>();
			if (textEditor == null)
				return;

			host = new XamlOutlineContentHost(editor);
			services = editor.GetRequiredService<IServiceContainer>();
			services.AddService(typeof(IOutlineContentHost), host);
		}

		public void Detach()
		{
			services?.RemoveService(typeof(IOutlineContentHost));

			host?.Dispose();
			host = null;
			services = null;
			textEditor = null;
		}
	}
}
