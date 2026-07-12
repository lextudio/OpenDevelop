using System;
using System.Collections.Generic;
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace ICSharpCode.SharpDevelop.LanguageServices.Lsp
{
	public sealed class LspCodeCompletionBinding : ICodeCompletionBinding
	{
		static readonly LspServerRegistry ServerRegistry = LspServerRegistry.CreateDefault();
		static readonly Dictionary<string, LspLanguageService> ServicesByExtension = new Dictionary<string, LspLanguageService>(StringComparer.OrdinalIgnoreCase);

		public CodeCompletionKeyPressResult HandleKeyPress(ITextEditor editor, char ch)
		{
			return CodeCompletionKeyPressResult.None;
		}

		public bool HandleKeyPressed(ITextEditor editor, char ch)
		{
			if (ch == '<' || ch == '.' || ch == ':' || ch == '=' || char.IsLetter(ch) || ch == '_')
				return ShowCompletion(editor);
			return false;
		}

		public bool CtrlSpace(ITextEditor editor)
		{
			return ShowCompletion(editor);
		}

		static bool ShowCompletion(ITextEditor editor)
		{
			if (editor == null || editor.FileName == null)
				return false;

			var service = GetService(editor.FileName);
			if (service == null)
				return false;

			var documentId = new DocumentId(editor.FileName);
			try {
				service.UpsertDocumentAsync(documentId, editor.Document.Text, System.Threading.CancellationToken.None)
					.GetAwaiter()
					.GetResult();

				var result = service.GetCompletionsAsync(documentId, editor.Caret.Offset, System.Threading.CancellationToken.None)
					.GetAwaiter()
					.GetResult();
				if (result.Items.Count == 0)
					return false;

				var list = LanguageServiceCompletionItemList.FromResult(result);
				editor.ShowCompletionWindow(list);
				return true;
			} catch (Exception ex) {
				LoggingService.Warn("LspCodeCompletionBinding: completion failed. " + ex.Message);
				return false;
			}
		}

		static LspLanguageService GetService(string fileName)
		{
			var extension = Path.GetExtension(fileName);
			if (!ServerRegistry.TryGetLaunchSpec(extension, out var spec))
				return null;

			if (!ServicesByExtension.TryGetValue(extension, out var service)) {
				var rootPath = FindWorkspaceRoot(fileName);
				service = new LspLanguageService(spec, new Uri(rootPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? rootPath : rootPath + Path.DirectorySeparatorChar).AbsoluteUri);
				ServicesByExtension[extension] = service;
			}

			return service;
		}

		static string FindWorkspaceRoot(string fileName)
		{
			var directory = Path.GetDirectoryName(fileName);
			return string.IsNullOrEmpty(directory) ? Environment.CurrentDirectory : directory;
		}
	}
}
