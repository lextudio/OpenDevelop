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
using System.IO;
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
		static readonly LspServerRegistry ServerRegistry = LspServerRegistry.CreateDefault();
		static readonly Dictionary<string, LspLanguageService> ServicesByExtension = new Dictionary<string, LspLanguageService>(StringComparer.OrdinalIgnoreCase);

		public static async Task<IReadOnlyList<DocumentOutlineNode>> GetOutlineAsync(ITextEditor editor, CancellationToken cancellationToken)
		{
			if (editor.FileName == null)
				return Array.Empty<DocumentOutlineNode>();

			var service = GetService(editor.FileName);
			if (service == null)
				return Array.Empty<DocumentOutlineNode>();

			var documentId = new DocumentId(editor.FileName);
			await service.UpsertDocumentAsync(documentId, editor.Document.Text, cancellationToken).ConfigureAwait(false);
			return await service.GetDocumentOutlineAsync(documentId, cancellationToken).ConfigureAwait(false);
		}

		static LspLanguageService GetService(string fileName)
		{
			string extension = Path.GetExtension(fileName);
			if (!ServerRegistry.TryGetLaunchSpec(extension, out var spec))
				return null;

			if (!ServicesByExtension.TryGetValue(extension, out var service)) {
				string rootPath = Path.GetDirectoryName(fileName);
				if (string.IsNullOrEmpty(rootPath))
					rootPath = Environment.CurrentDirectory;
				if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
					rootPath += Path.DirectorySeparatorChar;

				service = new LspLanguageService(spec, new Uri(rootPath).AbsoluteUri);
				ServicesByExtension[extension] = service;
			}

			return service;
		}
	}
}
