using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Designer.Surface;
using Xunit;
using Fact = Xunit.UIFactAttribute;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerToolboxHitTestTests
{
	[Fact]
	public void DisconnectedRowIsNotReportedAsHitTestable()
	{
		Assert.False(DesignerToolboxHitTest.WaitUntilHitTestableAt(new ListBoxItem(), timeoutMilliseconds: 0));
	}

	[Fact]
	public void DisconnectedRowWithAnOwningListIsNotReportedAsHitTestable()
	{
		var list = new ListBox();
		var row = new ListBoxItem();
		list.Items.Add(row);

		Assert.False(DesignerToolboxHitTest.WaitUntilHitTestableAt(list, row, timeoutMilliseconds: 0));
	}
}
