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
}
