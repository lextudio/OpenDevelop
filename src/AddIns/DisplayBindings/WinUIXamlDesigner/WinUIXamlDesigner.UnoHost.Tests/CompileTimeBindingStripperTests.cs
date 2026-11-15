using System.Xml.Linq;
using ICSharpCode.WinUIXamlDesigner.UnoHost;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost.Tests;

public sealed class CompileTimeBindingStripperTests
{
	const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	[Fact]
	public void Rewrites_root_and_template_member_paths_but_consumes_xDataType()
	{
		var root = XElement.Parse("""
			<Page xmlns="urn:test" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <TextBlock Text="{x:Bind Title, Mode=OneWay}" />
			  <ListView ItemsSource="{x:Bind Items}">
			    <ListView.ItemTemplate><DataTemplate x:DataType="local:Item">
			      <TextBlock Text="{x:Bind Name}" />
			    </DataTemplate></ListView.ItemTemplate>
			  </ListView>
			</Page>
			""");

		var result = CompileTimeBindingStripper.Strip(root);

		Assert.Equal(3, result.BindingsRewritten);
		Assert.Equal(0, result.BindingsDropped);
		Assert.Equal(1, result.DataTypesRemoved);
		Assert.Equal("{Binding Path=Title, Mode=OneWay}", root.Descendants().First().Attribute("Text")!.Value);
		Assert.Equal("{Binding Path=Items}", root.Descendants().ElementAt(1).Attribute("ItemsSource")!.Value);
		Assert.Equal("{Binding Path=Name}", root.Descendants().Last().Attribute("Text")!.Value);
		Assert.Null(root.Descendants().Single(element => element.Name.LocalName == "DataTemplate").Attribute(XName.Get("DataType", Xaml)));
	}

	[Fact]
	public void Drops_function_and_BindBack_expressions_instead_of_misrepresenting_them()
	{
		var root = XElement.Parse("""
			<Page xmlns="urn:test" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <TextBlock Text="{x:Bind Format(Title)}" ToolTip="{x:Bind Title, BindBack=SetTitle}" />
			</Page>
			""");

		var result = CompileTimeBindingStripper.Strip(root);

		Assert.Equal(0, result.BindingsRewritten);
		Assert.Equal(2, result.BindingsDropped);
		var text = root.Descendants().Single();
		Assert.Null(text.Attribute("Text"));
		Assert.Null(text.Attribute("ToolTip"));
	}

	[Fact]
	public void Evaluates_a_safe_root_instance_function_but_not_a_template_function()
	{
		var root = XElement.Parse("""
			<Page xmlns="urn:test" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <TextBlock Text="{x:Bind Shout(Title)}" />
			  <DataTemplate><TextBlock Text="{x:Bind Shout(Title)}" /></DataTemplate>
			</Page>
			""");

		var result = CompileTimeBindingStripper.Strip(root, designRoot: new FunctionPage());

		Assert.Equal(1, result.BindingsEvaluated);
		Assert.Equal(1, result.BindingsDropped);
		Assert.Equal("LIVE!", root.Descendants().First().Attribute("Text")!.Value);
		Assert.Null(root.Descendants().Last().Attribute("Text"));
	}

	[Fact]
	public void Evaluates_a_safe_root_function_with_multiple_member_arguments()
	{
		var root = XElement.Parse("""
			<Page xmlns="urn:test" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <TextBlock Text="{x:Bind Join(Title, Subtitle), Mode=OneWay}" />
			</Page>
			""");

		var result = CompileTimeBindingStripper.Strip(root, designRoot: new FunctionPage());

		Assert.Equal(1, result.BindingsEvaluated);
		Assert.Equal("live:preview", root.Descendants().Single().Attribute("Text")!.Value);
	}

	[Fact]
	public void Evaluates_a_safe_root_static_function_from_a_using_namespace()
	{
		var root = XElement.Parse("""
			<Page xmlns="urn:test" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="using:ICSharpCode.WinUIXamlDesigner.UnoHost.Tests">
			  <TextBlock Text="{x:Bind local:BindingHelpers.Wrap(Title)}" />
			</Page>
			""");

		var result = CompileTimeBindingStripper.Strip(root, designRoot: new FunctionPage());

		Assert.Equal(1, result.BindingsEvaluated);
		Assert.Equal("[live]", root.Descendants().Single().Attribute("Text")!.Value);
	}

	[Fact]
	public void Plans_a_complex_template_binding_without_overwriting_author_automation_id()
	{
		var root = XElement.Parse("""
			<Page xmlns="urn:test" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
			      xmlns:automation="using:Microsoft.UI.Xaml.Automation">
			  <DataTemplate><TextBlock automation:AutomationProperties.AutomationId="author-id"
			      Text="{x:Bind Name.ToUpper()}" /></DataTemplate>
			</Page>
			""");

		var result = CompileTimeBindingStripper.Strip(root);
		var target = root.Descendants().Single(element => element.Name.LocalName == "TextBlock");

		Assert.Single(result.TemplatePlans);
		Assert.Equal("author-id", target.Attribute(XName.Get("AutomationProperties.AutomationId", "using:Microsoft.UI.Xaml.Automation"))!.Value);
		Assert.Equal("__od_xbind_0", target.Attribute(XName.Get("TemplatePlanMarker.Token", "using:ICSharpCode.WinUIXamlDesigner.UnoHost"))!.Value);
		Assert.Null(target.Attribute("Text"));
	}



	[Fact]
	public void Can_keep_the_legacy_strip_only_mode_for_an_opted_out_preview()
	{
		var root = XElement.Parse("""
			<Page xmlns="urn:test" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
			  <TextBlock Text="{x:Bind Title}" />
			</Page>
			""");

		var result = CompileTimeBindingStripper.Strip(root, rewriteBindings: false);

		Assert.Equal(0, result.BindingsRewritten);
		Assert.Equal(1, result.BindingsDropped);
		Assert.Null(root.Descendants().Single().Attribute("Text"));
	}

}

sealed class FunctionPage
{
	public string Title => "live";
	public string Subtitle => "preview";
	public string Shout(string value) => value.ToUpperInvariant() + "!";
	public string Join(string first, string second) => first + ":" + second;
}


public static class BindingHelpers
{
	public static string Wrap(string value) => "[" + value + "]";
}
