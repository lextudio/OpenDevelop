using System;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.TypeSystem;

namespace CSharpBinding
{
	public class CSharpLanguageBinding : ILanguageBinding
	{
		public IFormattingStrategy FormattingStrategy {
			get { return DefaultFormattingStrategy.DefaultInstance; }
		}

		public IBracketSearcher BracketSearcher {
			get { return null; }
		}

		public CodeGenerator CodeGenerator {
			get { return null; }
		}

		public System.CodeDom.Compiler.CodeDomProvider CodeDomProvider {
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
