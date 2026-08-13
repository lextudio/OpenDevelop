// Translates the small subset of C# statement/expression shapes that WinForms-designer-generated
// InitializeComponent methods use into System.CodeDom - see RoslynDesignerLoader.cs's own doc
// comment for why this exists instead of a general C#-to-CodeDom converter.

using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpBinding.FormsDesigner
{
	class RoslynToCodeDomTranslator
	{
		readonly SemanticModel model;

		public RoslynToCodeDomTranslator(SemanticModel model)
		{
			this.model = model;
		}

		public IEnumerable<CodeStatement> TranslateStatement(StatementSyntax statement)
		{
			switch (statement) {
				case ExpressionStatementSyntax expr:
					return TranslateExpressionStatement(expr.Expression);
				case LocalDeclarationStatementSyntax local:
					return local.Declaration.Variables
						.Select(v => (CodeStatement)new CodeVariableDeclarationStatement(
							local.Declaration.Type.ToString(),
							v.Identifier.Text,
							v.Initializer != null ? TranslateExpression(v.Initializer.Value) : null));
				default:
					throw Unsupported(statement, "statement");
			}
		}

		IEnumerable<CodeStatement> TranslateExpressionStatement(ExpressionSyntax expression)
		{
			switch (expression) {
				case AssignmentExpressionSyntax assign when assign.IsKind(SyntaxKind.SimpleAssignmentExpression):
					yield return new CodeAssignStatement(TranslateExpression(assign.Left), TranslateExpression(assign.Right));
					yield break;

				case AssignmentExpressionSyntax attach when attach.IsKind(SyntaxKind.AddAssignmentExpression):
					yield return TranslateAttachEvent(attach);
					yield break;

				case InvocationExpressionSyntax invocation:
					yield return new CodeExpressionStatement(TranslateInvocation(invocation));
					yield break;

				default:
					throw Unsupported(expression, "top-level expression");
			}
		}

		CodeStatement TranslateAttachEvent(AssignmentExpressionSyntax attach)
		{
			if (attach.Left is not MemberAccessExpressionSyntax eventAccess)
				throw Unsupported(attach.Left, "event target");

			var targetObject = TranslateExpression(eventAccess.Expression);
			var eventName = eventAccess.Name.Identifier.Text;

			// Real designer-generated code always wraps the handler in "new EventHandlerType(...)",
			// but CodeAttachEventStatement only needs the method reference itself.
			ExpressionSyntax handlerExpr = attach.Right;
			if (handlerExpr is ObjectCreationExpressionSyntax handlerCreation && handlerCreation.ArgumentList?.Arguments.Count == 1)
				handlerExpr = handlerCreation.ArgumentList.Arguments[0].Expression;

			return new CodeAttachEventStatement(targetObject, eventName, TranslateExpression(handlerExpr));
		}

		CodeExpression TranslateInvocation(InvocationExpressionSyntax invocation)
		{
			var args = invocation.ArgumentList.Arguments.Select(a => TranslateExpression(a.Expression)).ToArray();

			if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
				return new CodeMethodInvokeExpression(TranslateExpression(memberAccess.Expression), memberAccess.Name.Identifier.Text, args);

			if (invocation.Expression is IdentifierNameSyntax identifier)
				return new CodeMethodInvokeExpression(new CodeThisReferenceExpression(), identifier.Identifier.Text, args);

			throw Unsupported(invocation, "method invocation target");
		}

		public CodeExpression TranslateExpression(ExpressionSyntax expression)
		{
			switch (expression) {
				case ThisExpressionSyntax:
					return new CodeThisReferenceExpression();

				case ObjectCreationExpressionSyntax creation:
					return new CodeObjectCreateExpression(
						ResolveTypeName(creation.Type),
						(creation.ArgumentList?.Arguments ?? default).Select(a => TranslateExpression(a.Expression)).ToArray());

				case InvocationExpressionSyntax invocation:
					return TranslateInvocation(invocation);

				case MemberAccessExpressionSyntax memberAccess:
					return TranslateMemberAccess(memberAccess);

				case IdentifierNameSyntax identifier:
					return TranslateIdentifier(identifier);

				case LiteralExpressionSyntax literal:
					return new CodePrimitiveExpression(literal.Token.Value);

				case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression) && unary.Operand is LiteralExpressionSyntax negLiteral:
					return new CodePrimitiveExpression(NegateNumeric(negLiteral.Token.Value));

				case CastExpressionSyntax cast:
					return new CodeCastExpression(ResolveTypeName(cast.Type), TranslateExpression(cast.Expression));

				case ParenthesizedExpressionSyntax paren:
					return TranslateExpression(paren.Expression);

				default:
					throw Unsupported(expression, "expression");
			}
		}

		CodeExpression TranslateMemberAccess(MemberAccessExpressionSyntax memberAccess)
		{
			var symbol = model.GetSymbolInfo(memberAccess).Symbol;
			var target = TranslateExpression(memberAccess.Expression);
			var name = memberAccess.Name.Identifier.Text;

			if (symbol is IFieldSymbol)
				return new CodeFieldReferenceExpression(target, name);
			// Properties, and anything unresolved (e.g. an enum member access like
			// AutoScaleMode.Font, where the "target" is really a type name) all serialize the
			// same way as a property reference in CodeDom.
			return new CodePropertyReferenceExpression(target, name);
		}

		CodeExpression TranslateIdentifier(IdentifierNameSyntax identifier)
		{
			var symbol = model.GetSymbolInfo(identifier).Symbol;
			var name = identifier.Identifier.Text;

			if (symbol is IFieldSymbol field && !field.IsStatic)
				return new CodeFieldReferenceExpression(new CodeThisReferenceExpression(), name);
			if (symbol is ILocalSymbol)
				return new CodeVariableReferenceExpression(name);
			if (symbol is INamedTypeSymbol typeSymbol)
				return new CodeTypeReferenceExpression(typeSymbol.ToDisplayString());

			// Unresolved (e.g. this translator's own throwaway single-file compilation can't see
			// every type) - fall back to treating it as a type reference, the common case for
			// identifiers appearing where designer code expects one (enum/static member access).
			return new CodeTypeReferenceExpression(name);
		}

		string ResolveTypeName(TypeSyntax type)
		{
			var symbol = model.GetTypeInfo(type).Type;
			return symbol?.ToDisplayString() ?? type.ToString();
		}

		static object NegateNumeric(object value) => value switch {
			int i => -i,
			double d => -d,
			float f => -f,
			long l => -l,
			_ => throw new NotSupportedException("Cannot negate non-numeric literal: " + value)
		};

		static NotSupportedException Unsupported(SyntaxNode node, string kind) =>
			new NotSupportedException($"RoslynToCodeDomTranslator: unsupported {kind} '{node}' ({node.Kind()}) - the WinForms designer's InitializeComponent translator only supports the shapes designer-generated code actually uses.");
	}
}
