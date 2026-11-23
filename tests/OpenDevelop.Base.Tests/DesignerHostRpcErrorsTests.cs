using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerHostRpcErrorsTests
{
	[Fact]
	public void StaleVersionIsRecognizedFromStructuredRpcData_NotTheMessage()
	{
		var data = new CommonErrorData { TypeName = typeof(DesignerStaleVersionException).FullName, Message = "localized text" };
		var stale = new RemoteInvocationException("different localized wrapper text", -32000, data, data);
		var other = new RemoteInvocationException("Stale version in an unrelated message", -32000,
			new CommonErrorData { TypeName = typeof(InvalidOperationException).FullName }, new CommonErrorData { TypeName = typeof(InvalidOperationException).FullName });

		Assert.True(DesignerHostRpcErrors.IsStaleVersion(stale));
		Assert.False(DesignerHostRpcErrors.IsStaleVersion(other));
	}

	[Theory]
	// The hosted-CI failure: the view saves with the version it holds (2) while the host and the recovery snapshot have
	// already moved to 3. The snapshot is newer, so it answers.
	[InlineData(3L, 2L, true)]
	// The window where the host advanced but the snapshot capture has not run yet.
	[InlineData(2L, 2L, true)]
	// A snapshot older than the version the view asks for cannot be the accepted source.
	[InlineData(1L, 2L, false)]
	// No snapshot yet (before the first open): nothing to fall back to, so the stale error stands.
	[InlineData(null, 2L, false)]
	public void RecoveryCanAnswerStaleFlush_OnlyWhenTheSnapshotIsNotOlderThanTheRequest(long? recoveryVersion, long requested, bool expected)
		=> Assert.Equal(expected, DesignerHostRpcErrors.RecoveryCanAnswerStaleFlush(recoveryVersion, requested));
}
