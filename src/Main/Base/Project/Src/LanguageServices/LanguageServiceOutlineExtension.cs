// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Generic ITextEditorExtension that attaches LanguageServiceOutlineContentHost to a text editor
// view - see that class's own doc comment. Registered per language addin (e.g.
// TypeScriptBinding.addin, CssBinding.addin) via its own <TextEditorExtension extensions="..."
// class="ICSharpCode.SharpDevelop.LanguageServices.LanguageServiceOutlineExtension" /> node;
// one shared implementation, no per-language subclass needed.

using System.ComponentModel.Design;

using ICSharpCode.AvalonEdit;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
	public sealed class LanguageServiceOutlineExtension : ITextEditorExtension
	{
		IServiceContainer services;
		LanguageServiceOutlineContentHost host;

		public void Attach(ITextEditor editor)
		{
			if (editor.GetService<TextEditor>() == null)
				return;

			host = new LanguageServiceOutlineContentHost(editor);
			services = editor.GetRequiredService<IServiceContainer>();
			services.AddService(typeof(IOutlineContentHost), host);
		}

		public void Detach()
		{
			services?.RemoveService(typeof(IOutlineContentHost));

			host?.Dispose();
			host = null;
			services = null;
		}
	}
}
