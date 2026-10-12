namespace CustomControlFixture;

[System.ComponentModel.ToolboxItemFilter("WpfDesigner")]
[System.ComponentModel.ToolboxItemFilter("WinFormsDesigner", System.ComponentModel.ToolboxItemFilterType.Prevent)]
[System.ComponentModel.ToolboxItemFilter("WpfSurface", System.ComponentModel.ToolboxItemFilterType.Require)]
public class GreetingBadge : System.Windows.Controls.ContentControl
{
}

/// <summary>A markup extension with only a positional constructor and no default constructor, so
/// the designer must convert its argument directly rather than through a mapped property.</summary>
public class EchoExtension : System.Windows.Markup.MarkupExtension
{
	readonly string text;

	public EchoExtension(string text) => this.text = text;

	public override object ProvideValue(System.IServiceProvider serviceProvider) => text;
}
