using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// <c>XamlReader.Parse(text, ParserContext)</c> as libraries call it: TomsToolbox.Wpf.Composition builds the data template of every view
/// model from a string with no <c>xmlns</c> at all, the prefixes only in <c>ParserContext.XmlnsDictionary</c>. The ILSpy add-in goes through
/// it, so when LibreWPF 0.1.0-preview.69.60.1 rebuilt the <c>XmlReader</c> handed to <c>XamlXmlReader</c> (dropping that context) the add-in
/// failed with "Prefix 'x' does not map to a namespace", and so did the packaged-app verification of release v5.9.4.
///
/// BAML and the XAML source generator are separate compile paths and the run-time reader is stock for both: it does not add implicit
/// namespaces (Simpler XAML is a build-time feature of the generator).
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class XamlReaderParserContextTests
{
	const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	static ParserContext ContextWithPrefixes()
	{
		var context = new ParserContext();
		context.XmlnsDictionary.Add("", Presentation);
		context.XmlnsDictionary.Add("x", Xaml);
		context.XmlnsDictionary.Add("local", $"clr-namespace:{typeof(XamlParserContextModel).Namespace};assembly={typeof(XamlParserContextModel).Assembly.GetName().Name}");
		return context;
	}

	[UIFact]
	public void ADataTemplateWhosePrefixesComeOnlyFromTheParserContext_Parses()
	{
		// The shape of TomsToolbox.Wpf.Composition.DataTemplateManager.CreateTemplate: no xmlns, `x` needed by the first attribute of the root.
		var xaml = "<DataTemplate DataType=\"{x:Type local:XamlParserContextModel}\"><TextBlock Text=\"{Binding}\" /></DataTemplate>";

		var template = Assert.IsType<DataTemplate>(XamlReader.Parse(xaml, ContextWithPrefixes()));

		Assert.Equal(typeof(XamlParserContextModel), template.DataType);
	}

	[UIFact]
	public void ADocumentThatDeclaresItsOwnNamespacesParsesWithOrWithoutAContext()
	{
		var xaml = $"<DataTemplate xmlns=\"{Presentation}\" xmlns:x=\"{Xaml}\" DataType=\"{{x:Type x:String}}\"><TextBlock Text=\"{{Binding}}\" /></DataTemplate>";

		Assert.Equal(typeof(string), Assert.IsType<DataTemplate>(XamlReader.Parse(xaml)).DataType);
		Assert.Equal(typeof(string), Assert.IsType<DataTemplate>(XamlReader.Parse(xaml, ContextWithPrefixes())).DataType);
	}

	[UIFact]
	public void TheRunTimeReaderDoesNotAddImplicitNamespaces()
	{
		// A document with no declarations and no context is an error here, exactly as in upstream WPF.
		Assert.ThrowsAny<Exception>(() => XamlReader.Parse("<DataTemplate DataType=\"{x:Type local:XamlParserContextModel}\"><TextBlock /></DataTemplate>"));
	}
}

/// <summary>A type of this assembly, reachable through a prefix that only the parser context knows (top level: a clr-namespace reference cannot name a nested type).</summary>
public sealed class XamlParserContextModel
{
}
