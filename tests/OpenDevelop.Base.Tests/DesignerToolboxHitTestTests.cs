using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Designer.Surface;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerToolboxHitTestTests
{
	[Xunit.UIFactAttribute]
	public void DisconnectedRowIsNotReportedAsHitTestable()
	{
		Assert.False(DesignerToolboxHitTest.WaitUntilHitTestableAt(new ListBoxItem(), timeoutMilliseconds: 0));
	}

	[Xunit.UIFactAttribute]
	public void DisconnectedRowWithAnOwningListIsNotReportedAsHitTestable()
	{
		var list = new ListBox();
		var row = new ListBoxItem();
		list.Items.Add(row);

		Assert.False(DesignerToolboxHitTest.WaitUntilHitTestableAt(list, row, timeoutMilliseconds: 0));
	}
}
