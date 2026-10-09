using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerPropertySemanticsTests
{
	[Theory]
	[InlineData("Unsupported")]
	[InlineData("Reference")]
	[InlineData("ReadOnly")]
	public void OpaqueKinds_AreReadOnlyEvenWhenHostFlagIsMissing(string kind)
	{
		Assert.True(DesignerPropertySemantics.IsReadOnly(new DesignerPropertyInfo { Kind = kind }));
	}

	[Theory]
	[InlineData("String")]
	[InlineData("Boolean")]
	[InlineData("ResourceImage")]
	public void ScalarKinds_RequireTheExplicitHostReadOnlyFlag(string kind)
	{
		Assert.False(DesignerPropertySemantics.IsReadOnly(new DesignerPropertyInfo { Kind = kind }));
		Assert.True(DesignerPropertySemantics.IsReadOnly(new DesignerPropertyInfo { Kind = kind, IsReadOnly = true }));
	}

	[Fact]
	public void NullProperty_IsRejected()
	{
		Assert.Throws<ArgumentNullException>(() => DesignerPropertySemantics.IsReadOnly(null!));
	}
}
