// MVP mocks: concrete NRefactory.Semantics ResolveResult subclasses. These are NOT ports of real NRefactory
// resolution semantics - they exist purely so that call sites in Base (GoToDefinition.cs, GotoDialog.cs,
// SymbolUnderCaretMenuCommand.cs, DeclaringTypeSubMenuBuilder.cs, SymbolTypeAtCaretConditionEvaluator.cs)
// keep compiling for a first-boot milestone. Per the MVP task's explicit mock policy: bodies favor
// straightforward field storage over throwing, but nothing here performs real type resolution.
using System;
using System.Collections.Generic;

namespace ICSharpCode.TypeSystem
{
	public class MemberResolveResult : ResolveResult
	{
		public IMember Member { get; }

		public MemberResolveResult(IType targetType, IMember member)
			: base(member != null ? member.ReturnType : targetType)
		{
			this.Member = member;
		}
	}

	public class TypeResolveResult : ResolveResult
	{
		public ITypeDefinition ResolvedClass { get; }

		public TypeResolveResult(ITypeDefinition resolvedClass)
			: base(resolvedClass)
		{
			this.ResolvedClass = resolvedClass;
		}
	}

	public class NamespaceResolveResult : ResolveResult
	{
		public INamespace Namespace { get; }

		public NamespaceResolveResult(INamespace ns)
			: base(null)
		{
			this.Namespace = ns;
		}
	}

	public class LocalResolveResult : ResolveResult
	{
		public IVariable Variable { get; }
		public string VariableName => Variable != null ? Variable.Name : null;
		public DomRegion VariableDefinitionRegion => Variable != null ? Variable.Region : DomRegion.Empty;
		public IField Field { get; }

		public LocalResolveResult(IVariable variable)
			: base(variable != null ? variable.Type : null)
		{
			this.Variable = variable;
		}
	}

	public class ErrorResolveResult : ResolveResult
	{
		public static readonly ErrorResolveResult UnknownError = new ErrorResolveResult();

		public ErrorResolveResult() : base(null)
		{
		}
	}

	/// <summary>
	/// Mock CallingClass/SyntaxTree properties on ResolveResult so older call sites (excluded RefactoringService
	/// files aside) that assumed every ResolveResult carries them keep compiling. Real NRefactory only put these
	/// on specific subclasses; here they're just always null/empty since nothing exercises real semantics.
	/// </summary>
	public static class ResolveResultExtensions
	{
		public static DomRegion GetDefinitionRegion(this ResolveResult result)
		{
			if (result is MemberResolveResult mrr && mrr.Member != null)
				return mrr.Member.Region;
			if (result is TypeResolveResult trr && trr.ResolvedClass != null)
				return trr.ResolvedClass.Region;
			if (result is LocalResolveResult lrr)
				return lrr.VariableDefinitionRegion;
			return DomRegion.Empty;
		}

		public static bool IsError(this ResolveResult result)
		{
			return result == null || result is ErrorResolveResult;
		}
	}
}
