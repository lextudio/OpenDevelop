using System.Text;

namespace LeXtudio.MewUI.Xaml;

/// <summary>How the C# generator emits a property value.</summary>
public enum MxamlPropertyKind
{
	/// <summary>Emitted as a quoted string literal.</summary>
	String,
	/// <summary>Emitted as an invariant numeric literal.</summary>
	Double,
	/// <summary>Emitted as an Int32 literal.</summary>
	Int32,
	/// <summary>Emitted as true/false.</summary>
	Boolean,
	/// <summary>Emitted as EnumType.Value (enum type == property name).</summary>
	Enum,
	/// <summary>Not emitted; any assignment surfaces a diagnostic instead.</summary>
	Unsupported,
}

/// <summary>One XML attribute: either a property or an event wiring, in source order.</summary>
public sealed class MxamlAttribute
{
	public required string Name { get; init; }
	public required string Value { get; set; }
	/// <summary>True when the attribute wires an event handler (value = method identifier).</summary>
	public bool IsEvent { get; init; }
	public int Line { get; init; }
	public int Column { get; init; }

	internal MxamlAttribute WithValue(string newValue) => new() { Name = Name, Value = newValue, IsEvent = IsEvent, Line = Line, Column = Column };
}

/// <summary>A control element in the .mxaml tree. Children are nested elements; the visual
/// containment contract is exactly the element nesting (no separate relationship calls).</summary>
public sealed class MxamlObject
{
	public required string Type { get; init; }
	public required string Name { get; set; }
	public int Line { get; init; }
	public int Column { get; init; }

	List<MxamlAttribute> attributes = new();
	List<MxamlObject> children = new();

	public IList<MxamlAttribute> Attributes => attributes;
	public IList<MxamlObject> Children => children;

	public MxamlAttribute? FindAttribute(string name) => attributes.FirstOrDefault(a => a.Name == name);
	public MxamlObject? FindChild(string name) => children.FirstOrDefault(c => c.Name == name);

	public IEnumerable<MxamlObject> DescendantsAndSelf()
	{
		yield return this;
		foreach (var descendant in children.SelectMany(c => c.DescendantsAndSelf()))
			yield return descendant;
	}

	internal void WriteTo(StringBuilder sb, string indent)
	{
		sb.Append(indent).Append('<').Append(Type);
		if (!string.IsNullOrEmpty(Name))
			sb.Append(" Name=\"").Append(Escape(Name)).Append('"');
		foreach (var a in attributes) {
			sb.Append(' ').Append(a.Name).Append("=\"").Append(Escape(a.Value)).Append('"');
		}
		if (children.Count == 0) {
			sb.Append(" />");
			return;
		}
		sb.Append(">\n");
		foreach (var c in children)
			c.WriteTo(sb, indent + "    ");
		sb.Append(indent).Append("</").Append(Type).Append(">\n");
	}

	static string Escape(string value) => value
		.Replace("&", "&amp;")
		.Replace("<", "&lt;")
		.Replace(">", "&gt;")
		.Replace("\"", "&quot;");
}
