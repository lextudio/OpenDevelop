#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace ICSharpCode.MewUIDesigner;

/// <summary>
/// Creates (or finds) the code-behind method an MXAML event attribute names, as the WPF designer
/// does on double-click. The isolated host only writes the attribute; the IDE edits the user's
/// <c>.mxaml.cs</c>. MewUI events are <c>Action</c>/<c>Action&lt;T&gt;</c> delegates whose
/// parameters depend on the control, so the parameter list comes from a table measured against
/// Aprillz.MewUI; an unlisted control/event pair binds the attribute but generates no method rather
/// than a signature that would not compile.
/// </summary>
public static partial class MewUICodeBehindHandlers
{
	static readonly Regex ClassAttribute = new("<Window\\b[^>]*\\bClass\\s*=\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);

	/// <summary>The handler's C# parameter list for <paramref name="eventName"/> on a control of
	/// <paramref name="controlType"/>, or null when the signature is not known.</summary>
	public static string? ParameterList(string controlType, string eventName) => (eventName, controlType) switch {
		("Click", _) => "",
		("Loaded" or "Closed" or "Activated" or "Deactivated", "Window") => "",
		("CheckedChanged", "CheckBox") => "bool? isChecked",
		("CheckedChanged", "RadioButton") => "bool isChecked",
		("SelectionChanged", "ComboBox" or "ListBox" or "TabControl") => "object? selectedItem",
		_ => null
	};

	/// <summary>The simple class name an MXAML document's root <c>Class</c> declares, or null.</summary>
	public static string? ClassNameOf(string? mxamlText)
	{
		var match = mxamlText == null ? Match.Empty : ClassAttribute.Match(mxamlText);
		return match.Success ? match.Groups[1].Value.Split('.').Last() : null;
	}

	/// <summary>Adds <c>private void handler(parameters) { }</c> to the class named
	/// <paramref name="className"/> (else the file's first class) unless a method of that name
	/// exists. Returns the new source (null when nothing was added) and the handler's 1-based line,
	/// added or found; 0 when there is no class.</summary>
	public static string? AddHandler(string source, string? className, string handlerName, string parameters, out int line)
	{
		line = 0;
		var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
		var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
		var target = classes.FirstOrDefault(c => c.Identifier.Text == className) ?? classes.FirstOrDefault();
		if (target == null)
			return null;
		var existing = target.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == handlerName);
		if (existing != null) {
			line = existing.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
			return null;
		}
		var method = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)), SyntaxFactory.Identifier(handlerName))
			.AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword))
			.WithParameterList(SyntaxFactory.ParseParameterList("(" + parameters + ")"))
			.WithBody(SyntaxFactory.Block())
			.WithAdditionalAnnotations(Formatter.Annotation);
		var updated = root.ReplaceNode(target, target.AddMembers(method));
		// Format only the inserted method, so the rest of the user's file keeps its layout.
		var formatted = Formatter.Format(updated, Formatter.Annotation, new AdhocWorkspace());
		var inserted = formatted.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == handlerName);
		line = inserted != null ? inserted.GetLocation().GetLineSpan().StartLinePosition.Line + 1 : 0;
		return formatted.ToFullString();
	}
}
