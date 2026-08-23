namespace LeXtudio.MewUI.Xaml;

/// <summary>Severity levels for MXAML diagnostics, mirroring compiler conventions.</summary>
public enum MxamlDiagnosticSeverity
{
	Info,
	Warning,
	Error,
}

/// <summary>A positioned problem in an .mxaml document. Parse errors carry line/column;
/// semantic diagnostics (unknown type, bad value, duplicate name) do the same via IXmlLineInfo.</summary>
public sealed record MxamlDiagnostic(
	MxamlDiagnosticSeverity Severity,
	string Message,
	int Line = 0,
	int Column = 0)
{
	public override string ToString()
		=> Line > 0 ? $"MX{(int)Severity:D4} ({Line},{Column}): {Message}" : $"MX{(int)Severity:D4}: {Message}";
}

/// <summary>Thrown by <see cref="MxamlDocument.Parse"/> when the document cannot be loaded at
/// all (malformed XML, wrong root). Semantic problems are reported as diagnostics instead.</summary>
public sealed class MxamlException : Exception
{
	public MxamlException(string message) : base(message) { }
	public MxamlException(string message, Exception inner) : base(message, inner) { }
}
