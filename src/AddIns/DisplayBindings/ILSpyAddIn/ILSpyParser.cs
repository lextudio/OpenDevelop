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

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.Search;
using ICSharpCode.SharpDevelop.Parser;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.TypeSystem;

using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.ILSpyAddIn
{
	public class ILSpyParser : IParser
	{
		public IReadOnlyList<string> TaskListTokens { get; set; }
		
		public bool CanParse(string fileName)
		{
			return fileName != null && fileName.StartsWith("ilspy://", StringComparison.OrdinalIgnoreCase);
		}
		
		public ITextSource GetFileContent(FileName fileName)
		{
			var result = ILSpyDecompilerService.DecompileType(DecompiledTypeReference.FromFileName(fileName));
			return new StringTextSource(result.Output, new OnDiskTextSourceVersion(DateTime.UtcNow));
		}
		
		public ParseInformation Parse(FileName fileName, ITextSource fileContent, bool fullParseInformationRequested, IProject parentProject, CancellationToken cancellationToken)
		{
			var reference = DecompiledTypeReference.FromFileName(fileName);
			DecompiledTypeResult result;
			if (fileContent != null) {
				result = new DecompiledTypeResult(fileContent.Text, new Dictionary<string, ICSharpCode.TypeSystem.TextLocation>());
			} else {
				result = ILSpyDecompilerService.DecompileType(reference, cancellationToken);
			}
			return new ILSpyParseInformation(new ILSpyUnresolvedFile(reference), fileContent != null ? fileContent.Version : null, result);
		}
		
		public ResolveResult Resolve(ParseInformation parseInfo, TextLocation location, ICompilation compilation, CancellationToken cancellationToken)
		{
			return ErrorResolveResult.UnknownError;
		}
		
		public ICodeContext ResolveContext(ParseInformation parseInfo, TextLocation location, ICompilation compilation, CancellationToken cancellationToken)
		{
			return null;
		}
		
		public ResolveResult ResolveSnippet(ParseInformation parseInfo, TextLocation location, string codeSnippet, ICompilation compilation, CancellationToken cancellationToken)
		{
			return ErrorResolveResult.UnknownError;
		}
		
		public void FindLocalReferences(ParseInformation parseInfo, ITextSource fileContent, IVariable variable, ICompilation compilation, Action<SearchResultMatch> callback, CancellationToken cancellationToken)
		{
		}
		
		public ICompilation CreateCompilationForSingleFile(FileName fileName, IUnresolvedFile unresolvedFile)
		{
			return null;
		}
	}
}
