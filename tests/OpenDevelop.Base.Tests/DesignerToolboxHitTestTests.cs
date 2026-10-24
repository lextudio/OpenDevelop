using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Designer.Surface;
using Xunit;
using UIFact = Xunit.UIFactAttribute;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerToolboxHitTestTests
{
	[UIFact]
	public void DisconnectedRowIsNotReportedAsHitTestable()
	{
		Assert.False(DesignerToolboxHitTest.WaitUntilHitTestableAt(new ListBoxItem(), timeoutMilliseconds: 0));
	}

	[UIFact]
	public void DisconnectedRowWithAnOwningListIsNotReportedAsHitTestable()
	{
		var list = new ListBox();
		var row = new ListBoxItem();
		list.Items.Add(row);

		Assert.False(DesignerToolboxHitTest.WaitUntilHitTestableAt(list, row, timeoutMilliseconds: 0));
	}
}
