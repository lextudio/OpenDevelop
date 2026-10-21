using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Designer.Remote;

/// <summary>Normalizes diagnostics received over the Designer Data Protocol for presentation.
/// A host may report the same parser failure through more than one recovery path; that must not
/// turn one source error into several identical lines in a designer status surface.</summary>
public static class DesignerDiagnosticSet
{
	/// <summary>Returns the first occurrence of every diagnostic identity in source order.
	/// Diagnostics at different source locations deliberately remain distinct, even when their
	/// messages are the same.</summary>
	public static IReadOnlyList<DesignerDiagnostic> Distinct(IEnumerable<DesignerDiagnostic> diagnostics)
	{
		ArgumentNullException.ThrowIfNull(diagnostics);
		var seen = new HashSet<DiagnosticIdentity>();
		var result = new List<DesignerDiagnostic>();
		foreach (var diagnostic in diagnostics) {
			if (diagnostic != null && seen.Add(new DiagnosticIdentity(diagnostic)))
				result.Add(diagnostic);
		}
		return result;
	}

	readonly record struct DiagnosticIdentity(string Severity, string Message, string FileName, int Line, int Column, int EndColumn)
	{
		public DiagnosticIdentity(DesignerDiagnostic diagnostic)
			: this(diagnostic.Severity ?? "", diagnostic.Message ?? "", diagnostic.FileName ?? "", diagnostic.Line, diagnostic.Column, diagnostic.EndColumn)
		{
		}
	}
}
