using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace WinUISample;

/// <summary>
/// Code-behind for the compiled-page probe. <see cref="Title"/> is read by <c>{x:Bind}</c> in
/// BoundPage.xaml, so it only has a value when the page is instantiated as compiled code (or when the
/// design-time evaluator reads it from a page instance). <see cref="Shout"/> and
/// <see cref="SuffixConverter"/> are the function/converter tiers of the same probe.
/// </summary>
public sealed partial class BoundPage : Page
{
	public BoundPage() => InitializeComponent();

	public string Title => "Bound from code-behind";

	public string Shout(string text) => text.ToUpperInvariant() + "!";

	// Non-text targets: the evaluator substitutes the text the XAML parser converts for the property type.
	public bool IsBusy => false;

	public double BoxWidth => 120.5;

	public Microsoft.UI.Xaml.Visibility State => Microsoft.UI.Xaml.Visibility.Collapsed;
}

/// <summary>Declared in BoundPage.xaml's resources; proves a converter the document declares is used.</summary>
public sealed class SuffixConverter : IValueConverter
{
	/// <summary>Set by the document itself (<c>Kind="markup"</c>), which the design-time evaluator applies too.</summary>
	public string Kind { get; set; } = "design";

	public object Convert(object value, Type targetType, object parameter, string language)
		=> $"{value} ({Kind}/{parameter})";

	public object ConvertBack(object value, Type targetType, object parameter, string language)
		=> throw new NotSupportedException();
}
