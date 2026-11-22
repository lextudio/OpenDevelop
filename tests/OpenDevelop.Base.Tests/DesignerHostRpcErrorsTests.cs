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
}
