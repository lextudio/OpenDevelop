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
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Keeps a control's code-behind field declaration (and every reference to it) in sync when
	/// the control's <c>x:Name</c> is changed from the WPF designer's property grid.
	/// </summary>
	public static class WpfControlRenameSync
	{
		static readonly LspServerRegistry ServerRegistry = LspServerRegistry.CreateDefault();
		static readonly Dictionary<string, LspLanguageService> ServicesByExtension = new Dictionary<string, LspLanguageService>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Renames the code-behind field for a control named <paramref name="oldName"/> to
		/// <paramref name="newName"/>, using the code-behind file (<c>&lt;xamlFileName&gt;.cs</c>)
		/// paired with <paramref name="xamlFileName"/>. No-ops silently if there's no code-behind
		/// file, no matching field, or no language service registered for its extension.
		/// </summary>
		public static async Task RenameAsync(string xamlFileName, string oldName, string newName)
		{
			if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName)
				return;

			string codeBehindFileName = xamlFileName + ".cs";
			if (!File.Exists(codeBehindFileName))
				return;

			var service = GetService(codeBehindFileName);
			if (service == null)
				return;

			try {
				string text = File.ReadAllText(codeBehindFileName);
				int offset = FindFieldOffset(text, oldName);
				if (offset < 0)
					return;

				var documentId = new DocumentId(codeBehindFileName);
				await service.UpsertDocumentAsync(documentId, text, CancellationToken.None).ConfigureAwait(false);

				var editsByFile = await service.RenameSymbolAsync(documentId, offset, newName, CancellationToken.None).ConfigureAwait(false);
				foreach (var pair in editsByFile)
					ApplyEdits(pair.Key, pair.Value);
			} catch (Exception ex) {
				LoggingService.Warn("WpfControlRenameSync: rename failed. " + ex.Message);
			}
		}

		static int FindFieldOffset(string text, string fieldName)
		{
			var match = Regex.Match(text, @"\b" + Regex.Escape(fieldName) + @"\b");
			return match.Success ? match.Index : -1;
		}

		static void ApplyEdits(string fileName, IReadOnlyList<TextEdit> edits)
		{
			if (edits.Count == 0 || !File.Exists(fileName))
				return;

			string text = File.ReadAllText(fileName);
			foreach (var edit in edits.OrderByDescending(e => e.Span.Start.Line).ThenByDescending(e => e.Span.Start.Column)) {
				int start = GetOffset(text, edit.Span.Start);
				int end = GetOffset(text, edit.Span.End);
				text = text.Substring(0, start) + edit.NewText + text.Substring(end);
			}

			File.WriteAllText(fileName, text);

			var openedFile = SD.FileService.GetOpenedFile(fileName);
			openedFile?.SetData(System.Text.Encoding.UTF8.GetBytes(text));

			SD.ParserService.ParseAsync(new FileName(fileName)).FireAndForget();
		}

		static int GetOffset(string text, TextPosition position)
		{
			int line = 1;
			int i = 0;
			while (i < text.Length && line < position.Line) {
				if (text[i] == '\n')
					line++;
				i++;
			}
			return Math.Min(text.Length, i + position.Column - 1);
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
