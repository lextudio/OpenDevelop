// Copyright (c) 2026 OpenDevelop contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.

using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vb = Microsoft.CodeAnalysis.VisualBasic;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace ICSharpCode.FormsDesigner.Services
{
	/// <summary>
	/// Produces the conventional WinForms <c>ComponentResourceManager.GetObject</c> binding for a
	/// newly created image resource. This is intentionally a syntax-only, parent-side operation:
	/// callers must apply its result together with the resource bytes in one document transaction.
	/// </summary>
	internal static class ResourceImageSourceBinding
	{
		public static bool TryAddCSharpBinding(string source, string componentName, string propertyName,
			string propertyTypeName, string resourceKey, out string updated, out string error)
		{
			updated = source;
			if (!ValidateArguments(componentName, propertyName, propertyTypeName, resourceKey, out error)) return false;
			var tree = CSharpSyntaxTree.ParseText(source);
			if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) {
				error = "The designer source has syntax errors.";
				return false;
			}
			var root = tree.GetCompilationUnitRoot();
			var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(item =>
				item.Identifier.ValueText == "InitializeComponent" && item.ParameterList.Parameters.Count == 0 && item.Body != null).ToArray();
			var method = methods.Length == 1 ? methods[0] : null;
			var classDeclaration = method?.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
			if (method == null || classDeclaration == null) {
				error = "The designer source has no unambiguous InitializeComponent method.";
				return false;
			}
			var target = CSharpTarget(componentName, propertyName);
			if (method.Body!.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(item => item.Left.ToString() == target)) {
				error = "The image property already has a source assignment.";
				return false;
			}
			var resourcesName = UniqueCSharpResourcesName(method.Body);
			var resourceDeclaration = SyntaxFactory.ParseStatement("var " + resourcesName
				+ " = new System.ComponentModel.ComponentResourceManager(typeof(" + classDeclaration.Identifier.ValueText + "));\n");
			var assignment = SyntaxFactory.ParseStatement(target + " = ((" + propertyTypeName + ")(" + resourcesName
				+ ".GetObject(" + SyntaxFactory.Literal(resourceKey).ToFullString() + ")));\n");
			var statements = method.Body.Statements.Insert(0, resourceDeclaration);
			var insertAt = statements.TakeWhile(item => !IsCSharpResumeLayout(item)).Count();
			statements = statements.Insert(insertAt, assignment);
			updated = root.ReplaceNode(method, method.WithBody(method.Body.WithStatements(statements))).ToFullString();
			error = "";
			return true;
		}

		public static bool TryAddVisualBasicBinding(string source, string componentName, string propertyName,
			string propertyTypeName, string resourceKey, out string updated, out string error)
		{
			updated = source;
			if (!ValidateArguments(componentName, propertyName, propertyTypeName, resourceKey, out error)) return false;
			var tree = Vb.VisualBasicSyntaxTree.ParseText(source);
			if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) {
				error = "The designer source has syntax errors.";
				return false;
			}
			var root = (VbSyntax.CompilationUnitSyntax)tree.GetRoot();
			var methods = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().Where(item =>
				item.BlockStatement is VbSyntax.MethodStatementSyntax statement
				&& statement.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword) && statement.Identifier.ValueText == "InitializeComponent"
				&& statement.ParameterList.Parameters.Count == 0).ToArray();
			var method = methods.Length == 1 ? methods[0] : null;
			var classBlock = method?.Ancestors().OfType<VbSyntax.ClassBlockSyntax>().FirstOrDefault();
			if (method == null || classBlock == null) {
				error = "The designer source has no unambiguous InitializeComponent method.";
				return false;
			}
			var target = VisualBasicTarget(componentName, propertyName);
			if (method.Statements.OfType<VbSyntax.AssignmentStatementSyntax>().Any(item => item.Left.ToString() == target)) {
				error = "The image property already has a source assignment.";
				return false;
			}
			var resourcesName = UniqueVisualBasicResourcesName(method);
			var declaration = Vb.SyntaxFactory.ParseExecutableStatement("Dim " + resourcesName + " As New System.ComponentModel.ComponentResourceManager(GetType("
				+ classBlock.BlockStatement.Identifier.ValueText + "))");
			var escapedKey = resourceKey.Replace("\"", "\"\"", StringComparison.Ordinal);
			var assignment = Vb.SyntaxFactory.ParseExecutableStatement(target + " = CType(" + resourcesName + ".GetObject(\"" + escapedKey + "\"), " + propertyTypeName + ")");
			var statements = method.Statements.Insert(0, declaration);
			var insertAt = statements.TakeWhile(item => !IsVisualBasicResumeLayout(item)).Count();
			statements = statements.Insert(insertAt, assignment);
			updated = root.ReplaceNode(method, method.WithStatements(statements)).ToFullString();
			error = "";
			return true;
		}

		static bool ValidateArguments(string componentName, string propertyName, string propertyTypeName, string resourceKey, out string error)
		{
			if ((componentName != "$this" && !IsIdentifier(componentName)) || !IsIdentifier(propertyName)
				|| !IsSupportedPropertyType(propertyTypeName) || String.IsNullOrWhiteSpace(resourceKey)) {
				error = "The component name, property name, type, and resource key must be valid.";
				return false;
			}
			error = "";
			return true;
		}

		static bool IsIdentifier(string value) => !String.IsNullOrEmpty(value) && SyntaxFacts.IsValidIdentifier(value);
		static bool IsSupportedPropertyType(string value) => value is "System.Drawing.Image" or "System.Drawing.Bitmap" or "System.Drawing.Icon";
		static string CSharpTarget(string componentName, string propertyName) => componentName == "$this" ? "this." + propertyName : "this." + componentName + "." + propertyName;
		static string VisualBasicTarget(string componentName, string propertyName) => componentName == "$this" ? "Me." + propertyName : "Me." + componentName + "." + propertyName;
		static string UniqueCSharpResourcesName(BlockSyntax body) => UniqueResourcesName(body.DescendantTokens().Select(token => token.ValueText), StringComparer.Ordinal);
		static string UniqueVisualBasicResourcesName(VbSyntax.MethodBlockSyntax method) => UniqueResourcesName(method.DescendantTokens().Select(token => token.ValueText), StringComparer.OrdinalIgnoreCase);
		static string UniqueResourcesName(System.Collections.Generic.IEnumerable<string> names, StringComparer comparer)
		{
			var set = names.ToHashSet(comparer);
			for (var suffix = 0;; suffix++) {
				var candidate = suffix == 0 ? "resources" : "resources" + suffix;
				if (!set.Contains(candidate)) return candidate;
			}
		}
		static bool IsCSharpResumeLayout(StatementSyntax statement) => statement.ToString().Contains("ResumeLayout", StringComparison.Ordinal);
		static bool IsVisualBasicResumeLayout(VbSyntax.StatementSyntax statement) => statement.ToString().Contains("ResumeLayout", StringComparison.OrdinalIgnoreCase);
	}
}
