using System.Windows.Markup;
using Xunit;

namespace OpenDevelop.XamlDom.Tests;

/// <summary>Guards the comparison itself: a dump that dropped a kind of content would make two different
/// graphs look equal.</summary>
public sealed class XamlGraphDumpTests
{
	[Fact]
	public void DumpIncludesStyleSetterTriggerAndBindingContent()
	{
		var dump = XamlGraphDump.Dump(XamlReader.Parse(XamlCorpus.Prefix + """
			<StackPanel.Resources>
			  <Style x:Key="S" TargetType="Button">
			    <Setter Property="Width" Value="80"/>
			    <Style.Triggers>
			      <Trigger Property="IsMouseOver" Value="True"><Setter Property="Height" Value="9"/></Trigger>
			      <DataTrigger Binding="{Binding Ok, Mode=OneWay}" Value="True"><Setter Property="Tag" Value="t"/></DataTrigger>
			    </Style.Triggers>
			  </Style>
			</StackPanel.Resources>
			<TextBox Text="{Binding Path=Name, ConverterParameter=p, ElementName=other, Mode=TwoWay}"/>
			</StackPanel>
			"""));
		foreach (var expected in new[] {
			"Style Button", "Setter FrameworkElement.Width = Double:80", "Trigger IsMouseOver = Boolean:True",
			"Setter FrameworkElement.Height = Double:9", "DataTrigger Binding(Path=Ok, Mode=OneWay", "Parameter=\"p\", ElementName=other" })
			Assert.True(dump.Contains(expected), "missing '" + expected + "' in:\n" + dump);
	}
}
