using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ICSharpCode.UnitTesting.Mtp
{
	// Microsoft.Testing.Platform's server-mode protocol reports each TestNode as a flat
	// dictionary with dotted/kebab keys (e.g. "location.file", "error.message",
	// "time.duration-ms") rather than nested JSON objects - see testfx's
	// Json.TestNodeSerializer.BuildTestNodeProperties. Wrapping the raw JsonElement instead of
	// binding to a fixed record keeps this forward-compatible with keys we don't know about yet.
	public sealed class MtpTestNode
	{
		readonly JsonElement _element;

		MtpTestNode(JsonElement element) => _element = element;

		public static MtpTestNode FromJson(JsonElement element) => new(element);

		public string Uid => GetString("uid") ?? string.Empty;

		public string DisplayName => GetString("display-name") ?? string.Empty;

		public string NodeType => GetString("node-type") ?? "group";

		public string? ExecutionState => GetString("execution-state");

		public string? ErrorMessage => GetString("error.message");

		public string? LocationFile => GetString("location.file");

		// Fully-qualified declaring type name, e.g. "MyProject.CalculatorTests". Populated by
		// MSTest and xUnit.v3 today; NUnit's MTP (VSTest-bridge) host reports no location.* keys at
		// all, so this - and LocationMethodName/LocationMethodParameterCount - are null for it.
		public string? LocationType => GetString("location.type");

		// Test-method name, without the declaring type. "location.method" embeds parameter types in
		// parens for a parameterized test method, e.g. "Divide(System.Int32)" - stripped here; use
		// LocationMethodParameterCount for the parameter count that implies.
		public string? LocationMethodName
		{
			get
			{
				var raw = GetString("location.method");
				if (raw is null)
					return null;
				var parenIndex = raw.IndexOf('(');
				return parenIndex >= 0 ? raw[..parenIndex] : raw;
			}
		}

		public int? LocationMethodParameterCount
		{
			get
			{
				var raw = GetString("location.method");
				if (raw is null)
					return null;
				var parenIndex = raw.IndexOf('(');
				if (parenIndex < 0)
					return 0;
				var closeIndex = raw.LastIndexOf(')');
				var inner = closeIndex > parenIndex ? raw[(parenIndex + 1)..closeIndex] : string.Empty;
				return inner.Length == 0 ? 0 : inner.Split(',').Length;
			}
		}

		public double? DurationMs => GetDouble("time.duration-ms");

		string? GetString(string propertyName)
			=> _element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

		double? GetDouble(string propertyName)
			=> _element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
				? value.GetDouble()
				: null;

		public override string ToString()
			=> string.Create(CultureInfo.InvariantCulture, $"{DisplayName} [{ExecutionState ?? NodeType}]");

		internal string RawJson => _element.GetRawText();

		// Minimal wire shape testing/runTests accepts for its "tests" filter. The host's TestNode
		// deserializer requires "display-name" and "node-type" to be present on every element (not
		// just "uid") or it throws server-side - kebab-case keys rule out a plain anonymous object.
		internal Dictionary<string, object?> ToFilterPayload() => new()
		{
			["uid"] = Uid,
			["display-name"] = DisplayName,
			["node-type"] = NodeType,
		};
	}
}
