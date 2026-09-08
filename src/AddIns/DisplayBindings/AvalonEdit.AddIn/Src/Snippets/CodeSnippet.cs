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
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.AvalonEdit.Snippets;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.AvalonEdit;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Refactoring;

namespace ICSharpCode.AvalonEdit.AddIn.Snippets
{
	/// <summary>
	/// A code snippet.
	/// </summary>
	public class CodeSnippet : AbstractFreezable, INotifyPropertyChanged
	{
		string name = string.Empty, description = string.Empty, text = string.Empty, keyword = string.Empty;
		
		public CodeSnippet()
		{
		}
		
		public CodeSnippet(CodeSnippet copy)
		{
			this.name = copy.name;
			this.description = copy.description;
			this.text = copy.text;
			this.keyword = copy.keyword;
		}
		
		public string Name {
			get { return name; }
			set {
				FreezableHelper.ThrowIfFrozen(this);
				if (name != value) {
					name = value ?? string.Empty;
					OnPropertyChanged("Name");
				}
			}
		}
		
		public string Text {
			get { return text; }
			set {
				FreezableHelper.ThrowIfFrozen(this);
				if (text != value) {
					text = value ?? string.Empty;
					OnPropertyChanged("Text");
				}
			}
		}
		
		public string Description {
			get { return description; }
			set {
				FreezableHelper.ThrowIfFrozen(this);
				if (description != value) {
					description = value ?? string.Empty;
					OnPropertyChanged("Description");
				}
			}
		}
		
		public bool HasSelection {
			get {
				return pattern.Matches(this.Text)
					.OfType<Match>()
					.Any(item => item.Value == "${Selection}");
			}
		}
		
		public string Keyword {
			get { return keyword; }
			set {
				FreezableHelper.ThrowIfFrozen(this);
				if (keyword != value) {
					keyword = value ?? string.Empty;
					OnPropertyChanged("Keyword");
				}
			}
		}
		
		public event PropertyChangedEventHandler PropertyChanged;
		
		protected virtual void OnPropertyChanged(string propertyName)
		{
			if (PropertyChanged != null) {
				PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
			}
		}
		
		public Snippet CreateAvalonEditSnippet(ITextEditor context)
		{
			return CreateAvalonEditSnippet(context, this.Text);
		}
		
		public ISnippetCompletionItem CreateCompletionItem(ITextEditor context)
		{
			return new SnippetCompletionItem(context, this) { AlwaysInsertSnippet = context.SelectionLength > 0 };
		}
		
		readonly static Regex pattern = new Regex(@"\$\{([^\}]*)\}", RegexOptions.CultureInvariant);
		
		public static Snippet CreateAvalonEditSnippet(ITextEditor context, string snippetText)
		{
			// Synchronous construction is for previews. Actual insertion resolves semantic
			// substitutions before editing through InsertAsync.
			return CreateAvalonEditSnippet(context, snippetText, null);
		}

		static Snippet CreateAvalonEditSnippet(ITextEditor context, string snippetText, string className)
		{
			if (snippetText == null)
				throw new ArgumentNullException("text");
			var replaceableElements = new Dictionary<string, SnippetReplaceableTextElement>(StringComparer.OrdinalIgnoreCase);
			foreach (Match m in pattern.Matches(snippetText)) {
				string val = m.Groups[1].Value;
				int equalsSign = val.IndexOf('=');
				if (equalsSign > 0) {
					string name = val.Substring(0, equalsSign);
					replaceableElements[name] = new SnippetReplaceableTextElement();
				}
			}
			Snippet snippet = new Snippet();
			int pos = 0;
			foreach (Match m in pattern.Matches(snippetText)) {
				if (pos < m.Index) {
					snippet.Elements.Add(new SnippetTextElement { Text = snippetText.Substring(pos, m.Index - pos) });
					pos = m.Index;
				}
				snippet.Elements.Add(CreateElementForValue(context, replaceableElements, m.Groups[1].Value, m.Index, snippetText, className));
				pos = m.Index + m.Length;
			}
			if (pos < snippetText.Length) {
				snippet.Elements.Add(new SnippetTextElement { Text = snippetText.Substring(pos) });
			}
			if (!snippet.Elements.Any(e => e is SnippetCaretElement)) {
				int index = snippet.Elements.FindIndex(e2 => e2 is SnippetSelectionElement);
				if (index > -1)
					snippet.Elements.Insert(index + 1, new SnippetCaretElement());
			}
			return snippet;
		}
		
		readonly static Regex functionPattern = new Regex(@"^([a-zA-Z]+)\(([^\)]*)\)$", RegexOptions.CultureInvariant);
		
		static SnippetElement CreateElementForValue(ITextEditor context, Dictionary<string, SnippetReplaceableTextElement> replaceableElements, string val, int offset, string snippetText, string className)
		{
			SnippetReplaceableTextElement srte;
			int equalsSign = val.IndexOf('=');
			if (equalsSign > 0) {
				string name = val.Substring(0, equalsSign);
				if (replaceableElements.TryGetValue(name, out srte)) {
					if (srte.Text == null)
						srte.Text = val.Substring(equalsSign + 1);
					return srte;
				}
			}
			
			foreach (ISnippetElementProvider provider in SnippetManager.Instance.SnippetElementProviders) {
				SnippetElement element = provider.GetElement(new SnippetInfo(val, snippetText, offset));
				if (element != null)
					return element;
			}
			
			if (replaceableElements.TryGetValue(val, out srte))
				return new SnippetBoundElement { TargetElement = srte };
			Match m = functionPattern.Match(val);
			if (m.Success) {
				Func<string, string> f = GetFunction(context, m.Groups[1].Value);
				if (f != null) {
					string innerVal = m.Groups[2].Value;
					if (replaceableElements.TryGetValue(innerVal, out srte))
						return new FunctionBoundElement { TargetElement = srte, function = f };
					string result2 = GetValue(innerVal, className);
					if (result2 != null)
						return new SnippetTextElement { Text = f(result2) };
					else
						return new SnippetTextElement { Text = f(innerVal) };
				}
			}
			string result = GetValue(val, className);
			if (result != null)
				return new SnippetTextElement { Text = result };
			else
				return new SnippetReplaceableTextElement { Text = val }; // ${unknown} -> replaceable element
		}
		
		static string GetValue(string propertyName, string className)
		{
			if ("ClassName".Equals(propertyName, StringComparison.OrdinalIgnoreCase)) {
				if (className != null)
					return className;
			}
			return Core.StringParser.GetValue(propertyName);
		}
		
		public async Task<bool> InsertAsync(ITextEditor editor, ICSharpCode.AvalonEdit.Editing.TextArea textArea,
			int start, int length, string activationMethod)
		{
			var document = editor.Document;
			var text = document.Text;
			var fileName = editor.FileName;
			var caret = editor.Caret.Offset;
			var selectionStart = editor.SelectionStart;
			var selectionLength = editor.SelectionLength;
			var snippetText = Text;
			string className = null;
			if (snippetText.IndexOf("ClassName", StringComparison.OrdinalIgnoreCase) >= 0) {
				var registry = SD.GetService<LanguageServiceRegistry>();
				if (registry != null && registry.TryGetService(fileName, out var service)) {
					var id = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(fileName);
					await service.UpsertDocumentAsync(id, text, CancellationToken.None);
					className = await service.GetContainingTypeNameAsync(id, caret, CancellationToken.None);
				}
			}
			// Never keep an undo transaction open over RPC, or remove the triggering word
			// until the reply is known to belong to this unchanged editor state.
			if (editor.Document != document || editor.FileName != fileName || document.Text != text
				|| editor.Caret.Offset != caret || editor.SelectionStart != selectionStart
				|| editor.SelectionLength != selectionLength)
				return false;
			var snippet = CreateAvalonEditSnippet(editor, snippetText, className);
			using (document.OpenUndoGroup()) {
				document.Remove(start, length);
				snippet.Insert(textArea);
			}
			TrackUsage(activationMethod);
			return true;
		}
		
		static Func<string, string> GetFunction(ITextEditor context, string name)
		{
			if ("toLower".Equals(name, StringComparison.OrdinalIgnoreCase))
				return s => s.ToLower();
			if ("toUpper".Equals(name, StringComparison.OrdinalIgnoreCase))
				return s => s.ToUpper();
			
			if ("toFieldName".Equals(name, StringComparison.OrdinalIgnoreCase))
				return s => context.Language.CodeGenerator.GetFieldName(s);
			if ("toPropertyName".Equals(name, StringComparison.OrdinalIgnoreCase))
				return s => context.Language.CodeGenerator.GetPropertyName(s);
			if ("toParameterName".Equals(name, StringComparison.OrdinalIgnoreCase))
				return s => context.Language.CodeGenerator.GetParameterName(s);
			return null;
		}
		
		sealed class FunctionBoundElement : SnippetBoundElement
		{
			internal Func<string, string> function;
			
			public override string ConvertText(string input)
			{
				return function(input);
			}
		}
		
		/// <summary>
		/// Reports the snippet usage to UDC
		/// </summary>
		internal void TrackUsage(string activationMethod)
		{
			bool isUserModified = !SnippetManager.Instance.defaultSnippets.Any(g => g.Snippets.Contains(this, CodeSnippetComparer.Instance));
			SD.AnalyticsMonitor.TrackFeature(typeof(CodeSnippet), isUserModified ? "usersnippet" : Name, activationMethod);
		}
	}
}
