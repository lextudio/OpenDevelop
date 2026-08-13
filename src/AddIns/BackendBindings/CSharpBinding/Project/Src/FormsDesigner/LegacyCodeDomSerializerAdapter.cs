using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CSharp;

namespace CSharpBinding.FormsDesigner
{
	/// <summary>
	/// Compatibility boundary for third-party controls that explicitly register CodeDomSerializer.
	/// CodeDOM objects never become the designer document model: they are converted immediately to
	/// Roslyn statements and discarded.
	/// </summary>
	sealed class LegacyCodeDomSerializerAdapter
	{
		readonly IDesignerSerializationManager manager;
		readonly CSharpCodeProvider provider = new();

		public LegacyCodeDomSerializerAdapter(IDesignerSerializationManager manager) => this.manager = manager;

		public bool IsRequired(Type componentType) => componentType
			.GetCustomAttributes(typeof(DesignerSerializerAttribute), true)
			.Cast<DesignerSerializerAttribute>()
			.Any(a => (a.SerializerBaseTypeName?.Contains("CodeDomSerializer", StringComparison.Ordinal) ?? false)
				|| (a.SerializerTypeName?.Contains("CodeDomSerializer", StringComparison.Ordinal) ?? false));

		public IReadOnlyList<StatementSyntax> Serialize(IComponent component)
		{
			var serializer = manager.GetSerializer(component.GetType(), typeof(CodeDomSerializer)) as CodeDomSerializer;
			if (serializer == null)
				throw new InvalidOperationException($"Control '{component.GetType().FullName}' declares a CodeDomSerializer, but the design host could not create it.");

			var serialized = serializer.Serialize(manager, component);
			var statements = serialized switch {
				CodeStatement statement => new[] { statement },
				CodeStatementCollection collection => collection.Cast<CodeStatement>().ToArray(),
				_ => throw new NotSupportedException($"Third-party serializer '{serializer.GetType().FullName}' returned unsupported '{serialized?.GetType().FullName ?? "null"}'.")
			};

			return statements.Select(ToRoslyn).ToArray();
		}

		StatementSyntax ToRoslyn(CodeStatement statement)
		{
			using var writer = new StringWriter();
			provider.GenerateCodeFromStatement(statement, writer, new CodeGeneratorOptions());
			var parsed = SyntaxFactory.ParseStatement(writer.ToString());
			if (parsed.ContainsDiagnostics)
				throw new InvalidOperationException("Third-party CodeDOM serializer generated invalid C#: " + writer);
			return (StatementSyntax)new RemoveRedundantThisRewriter().Visit(parsed);
		}

		sealed class RemoveRedundantThisRewriter : CSharpSyntaxRewriter
		{
			public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
			{
				if (node.Expression is ThisExpressionSyntax)
					return SyntaxFactory.IdentifierName(node.Name.Identifier).WithTriviaFrom(node);
				return base.VisitMemberAccessExpression(node);
			}
		}
	}
}
