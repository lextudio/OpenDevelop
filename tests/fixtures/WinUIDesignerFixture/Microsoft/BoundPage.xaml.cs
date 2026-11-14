using Microsoft.UI.Xaml.Controls;

namespace WinUISample;

/// <summary>
/// Code-behind for the compiled-page probe. <see cref="Title"/> is read by <c>{x:Bind}</c> in
/// BoundPage.xaml, so it only has a value when the page is instantiated as compiled code.
/// </summary>
public sealed partial class BoundPage : Page
{
	public BoundPage() => InitializeComponent();

	public string Title => "Bound from code-behind";
}
