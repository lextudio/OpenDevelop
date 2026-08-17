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
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.TypeSystem;

namespace CSharpBinding
{
	// Minimal ownership-correct registration (doc/technotes/csharp-vb-binding.md Phase 0): completion
	// itself is driven by RoslynCodeCompletionBinding, registered separately in CSharpBinding.addin, not
	// through CreateCompletionBinding here. The pre-Roslyn dot-completion-context-stub machinery this
	// class used to carry (NRefactory-based partial-class AST synthesis) is retired, not ported -
	// FormattingStrategy/BracketSearcher/CodeGenerator stay generic until their own migration phase.
	public class CSharpLanguageBinding : ILanguageBinding
	{
		// DefaultFormattingStrategy.DefaultInstance is internal to ICSharpCode.SharpDevelop; the
		// class itself is public and stateless, so a fresh instance here is equivalent.
		static readonly IFormattingStrategy defaultFormattingStrategy = new DefaultFormattingStrategy();

		public IFormattingStrategy FormattingStrategy {
			get { return defaultFormattingStrategy; }
		}

		public IBracketSearcher BracketSearcher {
			get { return DefaultBracketSearcher.DefaultInstance; }
		}

		public CodeGenerator CodeGenerator {
			get { return null; }
		}

		public ICodeCompletionBinding CreateCompletionBinding(string expressionToComplete, ICodeContext context)
		{
			return null;
		}

		public object GetService(Type serviceType)
		{
			return null;
		}
	}
}
