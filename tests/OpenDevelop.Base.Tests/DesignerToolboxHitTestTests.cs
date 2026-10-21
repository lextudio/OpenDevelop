using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Designer.Surface;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerToolboxHitTestTests
{
	[Fact]
	public void DisconnectedRowIsNotReportedAsHitTestable()
	{
		Assert.False(DesignerToolboxHitTest.WaitUntilHitTestableAt(new ListBoxItem(), timeoutMilliseconds: 0));
	}
}
