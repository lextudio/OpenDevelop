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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace ICSharpCode.XamlBinding
{
	/// <summary>
	/// Fetches a document's outline from its LSP language server (textDocument/documentSymbol),
	/// following the same per-extension service-lookup pattern as
	/// <c>LspCodeCompletionBinding</c> (upsert the current buffer, then call the feature).
	/// </summary>
	static class XamlOutlineLspProvider
	{
		public static async Task<IReadOnlyList<DocumentOutlineNode>> GetOutlineAsync(ITextEditor editor, CancellationToken cancellationToken)
		{
			if (editor.FileName == null)
				return Array.Empty<DocumentOutlineNode>();

			// LspServiceManager.GetService already caches per (languageId, workspace root) and
			// resolves the workspace root correctly (walks up from the file for a *.sln*/*.*proj
			// marker) - no need for a second private cache/registry here, and no risk of drifting
			// out of sync with whatever XamlBinding's RegisterXamlLanguageServiceCommand
			// registered for ".xaml" at addin startup.
			var service = LspServiceManager.GetService(editor.FileName);
			if (service == null)
				return Array.Empty<DocumentOutlineNode>();

			var documentId = new DocumentId(editor.FileName);
			await service.UpsertDocumentAsync(documentId, editor.Document.Text, cancellationToken).ConfigureAwait(false);
			return await service.GetDocumentOutlineAsync(documentId, cancellationToken).ConfigureAwait(false);
		}
	}
}
