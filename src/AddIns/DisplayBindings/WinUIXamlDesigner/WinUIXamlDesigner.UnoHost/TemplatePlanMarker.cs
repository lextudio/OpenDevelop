using Microsoft.UI.Xaml;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost;

/// <summary>
/// Private attached state used only in the repaired parser copy of a document.  A template has a
/// separate name scope for every item, so a normal x:Name cannot be used to recover a particular
/// target after materialisation.  This avoids repurposing author-owned automation metadata.
/// </summary>
public static class TemplatePlanMarker
{
	public static readonly DependencyProperty TokenProperty = DependencyProperty.RegisterAttached(
		"Token", typeof(string), typeof(TemplatePlanMarker), new PropertyMetadata(null));

	public static string? GetToken(DependencyObject element) => (string?)element.GetValue(TokenProperty);
	public static void SetToken(DependencyObject element, string? value) => element.SetValue(TokenProperty, value);
}
