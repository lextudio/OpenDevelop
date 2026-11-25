using ICSharpCode.SharpDevelop.Project.HotReload;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// <see cref="XamlExpressionSyntax"/> mirrors the rule of the LibreWPF XAML source generator (MAUI's expression syntax). These
/// cases are the generator's own (openavalon <c>XamlExpressionTests</c>): when one changes there, it changes here.
/// </summary>
public class XamlExpressionSyntaxTests
{
	[Theory]
	[InlineData("{Title}")]
	[InlineData("{= Title}")]
	[InlineData("{this.Title}")]
	[InlineData("{.Title}")]
	[InlineData("{Items.Count}")]
	[InlineData("{!IsActive}")]
	[InlineData("{$'{First} {Last}'}")]
	[InlineData("{(s, e) => Count++}")]
	[InlineData("{= Binding}")]
	[InlineData("{1 + 1}")]
	public void ABracedValueThatIsNotAMarkupExtension_IsAnExpression(string value)
		=> Assert.True(XamlExpressionSyntax.IsExpression(value));

	[Theory]
	[InlineData("{Binding Name}")]
	[InlineData("{Binding}")]
	[InlineData("{StaticResource Key}")]
	[InlineData("{DynamicResource Key}")]
	[InlineData("{TemplateBinding Width}")]
	[InlineData("{x:Static Foo.Bar}")]
	[InlineData("{x:Null}")]
	[InlineData("{local:Anything}")]
	[InlineData("{}{Escaped}")]
	[InlineData("{}")]
	[InlineData("plain")]
	[InlineData("OnClick")]
	[InlineData("")]
	[InlineData(null)]
	public void AMarkupExtension_OrAPlainValue_IsNot(string value)
		=> Assert.False(XamlExpressionSyntax.IsExpression(value));
}
