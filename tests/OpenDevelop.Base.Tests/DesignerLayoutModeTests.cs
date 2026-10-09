using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerLayoutModeTests
{
	[Theory]
	[InlineData("System.Windows.Controls.Canvas", DesignerLayoutMode.Absolute)]
	[InlineData("Grid", DesignerLayoutMode.Grid)]
	[InlineData("StackPanel", DesignerLayoutMode.Stack)]
	[InlineData("System.Windows.Forms.TableLayoutPanel", DesignerLayoutMode.Grid)]
	[InlineData("FlowLayoutPanel", DesignerLayoutMode.Flow)]
	[InlineData("ContentPresenter", DesignerLayoutMode.Content)]
	[InlineData("ThirdParty.WidgetHost", DesignerLayoutMode.Unknown)]
	public void InferFromContainerType_OnlyClassifiesKnownContainerContracts(string type, string expected)
	{
		Assert.Equal(expected, DesignerLayoutMode.InferFromContainerType(type));
	}
}
