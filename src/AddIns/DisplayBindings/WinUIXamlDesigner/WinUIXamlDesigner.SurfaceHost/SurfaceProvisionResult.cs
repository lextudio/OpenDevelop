using System.Text.Json;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost;

public enum SurfaceProvisionStatus
{
	/// <summary>A matched host was built and verified.</summary>
	Built,
	/// <summary>An identical request was served from the cache after revalidation.</summary>
	Cached,
	/// <summary>Only a bundled fallback exists. This is NOT a matched host.</summary>
	Degraded,
	Failed,
}

/// <summary>
/// The single JSON object the upstream SurfaceProvisioner CLI writes to stdout (contract version 1).
/// </summary>
public sealed record SurfaceProvisionResult(
	SurfaceProvisionStatus Status,
	bool Success,
	string? Error,
	bool Cancelled,
	string? DiscoveredVersion,
	string? HostDirectory,
	string? HostExePath,
	string? MergedPriPath,
	bool MatchedHost,
	bool CacheHit,
	double TotalSeconds)
{
	/// <summary>
	/// True only for a matched host the caller may launch with its project's resources. A degraded bundled
	/// fallback, a failure and an unknown status are never matched, so a host is never silently substituted.
	/// </summary>
	public bool IsMatched => Success && MatchedHost && Status is SurfaceProvisionStatus.Built or SurfaceProvisionStatus.Cached
		&& HostExePath != null && MergedPriPath != null;

	public static SurfaceProvisionResult Parse(string stdout)
	{
		if (string.IsNullOrWhiteSpace(stdout))
			return Failure("The provisioner wrote nothing to stdout.");
		try {
			using var doc = JsonDocument.Parse(stdout);
			var r = doc.RootElement;
			if (r.ValueKind != JsonValueKind.Object)
				return Failure("The provisioner result is not a JSON object.");
			if (Int(r, "contractVersion") != 1)
				return Failure("Unsupported provisioner contract version " + Int(r, "contractVersion") + ".");
			var status = Str(r, "status") switch {
				"built" => SurfaceProvisionStatus.Built,
				"cached" => SurfaceProvisionStatus.Cached,
				"degraded" => SurfaceProvisionStatus.Degraded,
				_ => SurfaceProvisionStatus.Failed,
			};
			bool success = Bool(r, "success");
			// "built"/"cached" must also say success; anything else is a failure whatever the status string claims.
			if (status is SurfaceProvisionStatus.Built or SurfaceProvisionStatus.Cached && !success)
				status = SurfaceProvisionStatus.Failed;
			return new SurfaceProvisionResult(
				status, success && status != SurfaceProvisionStatus.Failed, Str(r, "error"), Bool(r, "cancelled"),
				Str(r, "discoveredVersion"), Str(r, "hostDir"), Str(r, "hostExePath"), Str(r, "mergedPriPath"),
				Bool(r, "matchedHost"), Bool(r, "cacheHit"), Dbl(r, "totalSeconds"));
		} catch (JsonException ex) {
			return Failure("The provisioner result is not valid JSON: " + ex.Message);
		}
	}

	static SurfaceProvisionResult Failure(string error)
		=> new(SurfaceProvisionStatus.Failed, false, error, false, null, null, null, null, false, false, 0);

	static string? Str(JsonElement e, string n)
		=> e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

	static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;

	static int Int(JsonElement e, string n)
		=> e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : 0;

	static double Dbl(JsonElement e, string n)
		=> e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v) ? v : 0;
}
