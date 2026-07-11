using System.Text.Json;

namespace ICSharpCode.UnitTesting.Mtp
{
	// Parsed from the "initialize" response. MTP's server-mode protocol has no version-numbered
	// method surface to branch on (JsonRpcMethods.cs has stayed a single flat set since it shipped);
	// callers are expected to feature-detect via these capability flags instead.
	public sealed record MtpServerCapabilities(
		string ServerName,
		string ServerVersion,
		bool SupportsDiscovery,
		bool SupportsMultiRequest,
		bool SupportsVsTestProvider)
	{
		public static MtpServerCapabilities FromJson(JsonElement initializeResponse)
		{
			var serverInfo = initializeResponse.TryGetProperty("serverInfo", out var si) ? si : default;
			var testingCapabilities = initializeResponse.TryGetProperty("capabilities", out var caps)
				&& caps.TryGetProperty("testing", out var testing)
					? testing
					: default;

			return new MtpServerCapabilities(
				ServerName: GetString(serverInfo, "name") ?? "(unknown)",
				ServerVersion: GetString(serverInfo, "version") ?? "0.0.0",
				SupportsDiscovery: GetBool(testingCapabilities, "supportsDiscovery"),
				SupportsMultiRequest: GetBool(testingCapabilities, "experimental_multiRequestSupport"),
				SupportsVsTestProvider: GetBool(testingCapabilities, "vstestProvider"));
		}

		static string? GetString(JsonElement element, string propertyName)
			=> element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

		static bool GetBool(JsonElement element, string propertyName)
			=> element.ValueKind == JsonValueKind.Object
				&& element.TryGetProperty(propertyName, out var value)
				&& (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
				&& value.GetBoolean();
	}
}
