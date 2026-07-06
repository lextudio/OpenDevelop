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

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.TypeSystem;

namespace Debugger.AddIn.Pads.Controls
{
	/// <summary>
	/// Dot-completion for expressions typed into the debug console / watch / breakpoint condition
	/// editors. This goes through the same language-service abstraction (<see cref="SD.LanguageService"/>)
	/// used by the regular code editor, which in this repo resolves to a Roslyn-backed C# language
	/// service - not NRefactory. There is nothing DAP/ICorDebug-specific here; only the (unused,
	/// NRefactory-CSharpParser-based) syntax-check helper that used to live in this class was removed.
	/// </summary>
	static class DebuggerDotCompletion
	{
		public static ICodeCompletionBinding PrepareDotCompletion(string expressionToComplete, ICodeContext context)
		{
			return SD.LanguageService.GetLanguageByExtension(".cs")
				.CreateCompletionBinding(expressionToComplete, context);
		}
	}
}
