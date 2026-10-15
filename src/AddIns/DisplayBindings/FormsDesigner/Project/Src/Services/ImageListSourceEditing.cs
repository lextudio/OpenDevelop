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
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vb = Microsoft.CodeAnalysis.VisualBasic;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace ICSharpCode.FormsDesigner.Services
{
	/// <summary>
	/// Rewrites the designer source for an edited ImageList: the conventional
	/// <c>imageList1.ImageStream = (ImageListStreamer)(resources.GetObject("imageList1.ImageStream"))</c> binding and one
	/// <c>imageList1.Images.SetKeyName(i, "key")</c> per named image. A syntax-only, parent-side operation: callers apply the result
	/// together with the resource bytes in one document transaction. An empty key list removes the stream binding and every key.
	/// </summary>
	internal static class ImageListSourceEditing
	{
		const string StreamerType = "System.Windows.Forms.ImageListStreamer";

		public static bool TryRewriteCSharp(string source, string imageListName, string resourceKey, IReadOnlyList<string> keys,
			out string updated, out string error)
		{
			updated = source;
			if (!Validate(imageListName, resourceKey, out error)) return false;
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
			static string Normalize(ExpressionSyntax expression) => expression.ToString().Replace("this.", "", StringComparison.Ordinal);
			var streamTarget = imageListName + ".ImageStream";
			var imagesTarget = imageListName + ".Images";
			var statements = method.Body!.Statements.ToList();
			bool IsStream(StatementSyntax statement) => statement is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
				&& Normalize(assignment.Left) == streamTarget;
			bool IsKeyName(StatementSyntax statement) => statement is ExpressionStatementSyntax {
				Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SetKeyName" } access } }
				&& Normalize(access.Expression) == imagesTarget;
			var existingStream = statements.FindIndex(IsStream);
			// The stream's own key must be the one being written: another key means another resource this edit does not own.
			if (existingStream >= 0 && !statements[existingStream].ToString().Contains("\"" + resourceKey + "\"", StringComparison.Ordinal)) {
				error = "The image list's ImageStream is bound to a different resource key.";
				return false;
			}
			if (keys.Count == 0) {
				updated = Rebuild(root, method, statements.Where(item => !IsStream(item) && !IsKeyName(item)));
				return true;
			}
			var resourcesName = FindResourcesLocal(method.Body);
			var created = new List<StatementSyntax>();
			if (resourcesName == null) {
				resourcesName = UniqueName(method.Body.DescendantTokens().Select(token => token.ValueText), StringComparer.Ordinal);
				created.Add(SyntaxFactory.ParseStatement("var " + resourcesName + " = new System.ComponentModel.ComponentResourceManager(typeof("
					+ classDeclaration.Identifier.ValueText + "));\n"));
			}
			var creationIndex = statements.FindIndex(item => item is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment }
				&& Normalize(assignment.Left) == imageListName);
			if (existingStream < 0 && creationIndex < 0) {
				error = "The designer source never creates the image list '" + imageListName + "'.";
				return false;
			}
			var stream = existingStream >= 0 ? statements[existingStream] : SyntaxFactory.ParseStatement("this." + streamTarget + " = (("
				+ StreamerType + ")(" + resourcesName + ".GetObject(" + SyntaxFactory.Literal(resourceKey).ToFullString() + ")));\n");
			var names = new List<StatementSyntax>();
			for (var index = 0; index < keys.Count; index++)
				if (!String.IsNullOrEmpty(keys[index]))
					names.Add(SyntaxFactory.ParseStatement("this." + imagesTarget + ".SetKeyName(" + index + ", " + SyntaxFactory.Literal(keys[index]).ToFullString() + ");\n"));
			// Everything but the old key statements, with the stream binding and the keys where the stream was (or right after the creation).
			var kept = new List<StatementSyntax>();
			for (var index = 0; index < statements.Count; index++) {
				if (IsKeyName(statements[index]))
					continue;
				kept.Add(statements[index]);
				if (index == existingStream)
					kept.AddRange(names);
				else if (existingStream < 0 && index == creationIndex) {
					kept.Add(stream);
					kept.AddRange(names);
				}
			}
			updated = Rebuild(root, method, created.Concat(kept));
			error = "";
			return true;
		}

		public static bool TryRewriteVisualBasic(string source, string imageListName, string resourceKey, IReadOnlyList<string> keys,
			out string updated, out string error)
		{
			updated = source;
			if (!Validate(imageListName, resourceKey, out error)) return false;
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
			static string Normalize(VbSyntax.ExpressionSyntax expression) => expression.ToString().Replace("Me.", "", StringComparison.OrdinalIgnoreCase);
			var streamTarget = imageListName + ".ImageStream";
			var imagesTarget = imageListName + ".Images";
			var statements = method.Statements.ToList();
			bool IsStream(VbSyntax.StatementSyntax statement) => statement is VbSyntax.AssignmentStatementSyntax assignment
				&& String.Equals(Normalize(assignment.Left), streamTarget, StringComparison.OrdinalIgnoreCase);
			bool IsKeyName(VbSyntax.StatementSyntax statement) => statement is VbSyntax.ExpressionStatementSyntax {
				Expression: VbSyntax.InvocationExpressionSyntax { Expression: VbSyntax.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SetKeyName" } access } }
				&& access.Expression != null && String.Equals(Normalize(access.Expression), imagesTarget, StringComparison.OrdinalIgnoreCase);
			var existingStream = statements.FindIndex(IsStream);
			if (existingStream >= 0 && !statements[existingStream].ToString().Contains("\"" + resourceKey.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"", StringComparison.Ordinal)) {
				error = "The image list's ImageStream is bound to a different resource key.";
				return false;
			}
			if (keys.Count == 0) {
				updated = Rebuild(root, method, statements.Where(item => !IsStream(item) && !IsKeyName(item)));
				return true;
			}
			var resourcesName = FindResourcesLocal(method);
			var created = new List<VbSyntax.StatementSyntax>();
			if (resourcesName == null) {
				resourcesName = UniqueName(method.DescendantTokens().Select(token => token.ValueText), StringComparer.OrdinalIgnoreCase);
				created.Add(Vb.SyntaxFactory.ParseExecutableStatement("Dim " + resourcesName + " As New System.ComponentModel.ComponentResourceManager(GetType("
					+ classBlock.BlockStatement.Identifier.ValueText + "))"));
			}
			var creationIndex = statements.FindIndex(item => item is VbSyntax.AssignmentStatementSyntax assignment
				&& String.Equals(Normalize(assignment.Left), imageListName, StringComparison.OrdinalIgnoreCase));
			if (existingStream < 0 && creationIndex < 0) {
				error = "The designer source never creates the image list '" + imageListName + "'.";
				return false;
			}
			var escapedKey = resourceKey.Replace("\"", "\"\"", StringComparison.Ordinal);
			var stream = existingStream >= 0 ? statements[existingStream] : Vb.SyntaxFactory.ParseExecutableStatement("Me." + streamTarget + " = CType("
				+ resourcesName + ".GetObject(\"" + escapedKey + "\"), " + StreamerType + ")");
			var names = new List<VbSyntax.StatementSyntax>();
			for (var index = 0; index < keys.Count; index++)
				if (!String.IsNullOrEmpty(keys[index]))
					names.Add(Vb.SyntaxFactory.ParseExecutableStatement("Me." + imagesTarget + ".SetKeyName(" + index + ", \"" + keys[index].Replace("\"", "\"\"", StringComparison.Ordinal) + "\")"));
			var kept = new List<VbSyntax.StatementSyntax>();
			for (var index = 0; index < statements.Count; index++) {
				if (IsKeyName(statements[index]))
					continue;
				kept.Add(statements[index]);
				if (index == existingStream)
					kept.AddRange(names);
				else if (existingStream < 0 && index == creationIndex) {
					kept.Add(stream);
					kept.AddRange(names);
				}
			}
			updated = Rebuild(root, method, created.Concat(kept));
			error = "";
			return true;
		}

		static string Rebuild(CompilationUnitSyntax root, MethodDeclarationSyntax method, IEnumerable<StatementSyntax> statements)
			=> root.ReplaceNode(method, method.WithBody(method.Body!.WithStatements(SyntaxFactory.List(statements)))).ToFullString();

		static string Rebuild(VbSyntax.CompilationUnitSyntax root, VbSyntax.MethodBlockSyntax method, IEnumerable<VbSyntax.StatementSyntax> statements)
			=> root.ReplaceNode(method, method.WithStatements(Vb.SyntaxFactory.List(statements))).ToFullString();

		/// <summary>The name of the form's own <c>ComponentResourceManager</c> local (Visual Studio's <c>resources</c>), or null.</summary>
		static string? FindResourcesLocal(BlockSyntax body) => body.Statements.OfType<LocalDeclarationStatementSyntax>()
			.SelectMany(local => local.Declaration.Variables.Where(variable => local.Declaration.Type.ToString().EndsWith("ComponentResourceManager", StringComparison.Ordinal)
				|| variable.Initializer?.Value.ToString().Contains("ComponentResourceManager", StringComparison.Ordinal) == true))
			.Select(variable => variable.Identifier.ValueText).FirstOrDefault();

		static string? FindResourcesLocal(VbSyntax.MethodBlockSyntax method) => method.Statements.OfType<VbSyntax.LocalDeclarationStatementSyntax>()
			.SelectMany(local => local.Declarators.Where(declarator => declarator.ToString().Contains("ComponentResourceManager", StringComparison.Ordinal)))
			.SelectMany(declarator => declarator.Names).Select(name => name.Identifier.ValueText).FirstOrDefault();

		static string UniqueName(IEnumerable<string> existing, StringComparer comparer)
		{
			var set = existing.ToHashSet(comparer);
			for (var suffix = 0;; suffix++) {
				var candidate = suffix == 0 ? "resources" : "resources" + suffix;
				if (!set.Contains(candidate)) return candidate;
			}
		}

		static bool Validate(string imageListName, string resourceKey, out string error)
		{
			if (!SyntaxFacts.IsValidIdentifier(imageListName ?? "") || String.IsNullOrWhiteSpace(resourceKey)) {
				error = "The image list name and resource key must be valid.";
				return false;
			}
			error = "";
			return true;
		}
	}
}
