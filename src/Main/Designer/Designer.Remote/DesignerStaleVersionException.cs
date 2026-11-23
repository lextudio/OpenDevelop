namespace ICSharpCode.SharpDevelop.Designer.Remote;

/// <summary>Raised by a design host when a request's optimistic-concurrency version is obsolete.</summary>
public sealed class DesignerStaleVersionException : System.InvalidOperationException
{
	public DesignerStaleVersionException(long candidate, long current)
		: base($"Stale version {candidate}; current is {current}.") { }
}

/// <summary>Structured JSON-RPC classification shared by designer clients.</summary>
public static class DesignerHostRpcErrors
{
	public static bool IsStaleVersion(System.Exception exception) => exception is StreamJsonRpc.RemoteInvocationException remote
		&& remote.DeserializedErrorData is StreamJsonRpc.Protocol.CommonErrorData error
		&& error.TypeName == typeof(DesignerStaleVersionException).FullName;

	/// <summary>
	/// Whether the parent's recovery snapshot can stand in for a flush the host rejected as stale. A snapshot is advanced to
	/// the host's new version as soon as a mutation is accepted, which happens before the view assigns the state that
	/// mutation returned: a save in that window asks for the version the view still holds, one behind the snapshot. The
	/// snapshot is then the newer accepted source, so it answers the flush. A snapshot OLDER than the requested version
	/// is not (the view claims to know more than the parent recorded), and there is none before the first open.
	/// </summary>
	public static bool RecoveryCanAnswerStaleFlush(long? recoveryVersion, long requestedVersion) => recoveryVersion >= requestedVersion;
}
