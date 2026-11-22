using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Xceed.Wpf.Toolkit;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The shell's two theme dictionaries (Theme.Light.xaml, Theme.Dark.xaml) replace the framework templates of the
/// controls the IDE uses. A template that lacks a part the control expects fails only when somebody uses that
/// control, which is how an IsEditable ComboBox lost its text box (the Android SDK Manager's location box stayed
/// blank) and how the toolkit's BusyIndicator stayed white-on-white in the dark theme. These load each dictionary
/// from the repository and apply the templates the way a control does.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class ThemeControlTemplateTests
{
	public static TheoryData<string> Themes => new() { "Light", "Dark" };

	static string ThemePath(string theme) =>
		Path.Combine(ProductCopyrightTests.RepositoryRoot(), "src", "Main", "SharpDevelop", "Themes", $"Theme.{theme}.xaml");

	static ResourceDictionary Load(string theme)
	{
		using var stream = File.OpenRead(ThemePath(theme));
		return (ResourceDictionary)XamlReader.Load(stream);
	}

	static Style StyleFor(ResourceDictionary themeDictionary, Type target)
	{
		Assert.True(themeDictionary.Contains(target), $"The theme has no implicit style for {target.Name}.");
		return Assert.IsType<Style>(themeDictionary[target]);
	}

	[UITheory]
	[MemberData(nameof(Themes))]
	public void ComboBox_HasAnEditableTextBoxThatOnlyAnEditableComboBoxShows(string theme)
	{
		ResourceDictionary dictionary = Load(theme);
		Style style = StyleFor(dictionary, typeof(ComboBox));

		var plain = new ComboBox { Style = style, IsEditable = false };
		plain.Resources.MergedDictionaries.Add(dictionary);
		plain.ApplyTemplate();
		var plainBox = Assert.IsType<TextBox>(plain.Template.FindName("PART_EditableTextBox", plain));
		Assert.Equal(Visibility.Collapsed, plainBox.Visibility);

		var editable = new ComboBox { Style = style, IsEditable = true };
		editable.Resources.MergedDictionaries.Add(dictionary);
		editable.ApplyTemplate();
		var editableBox = Assert.IsType<TextBox>(editable.Template.FindName("PART_EditableTextBox", editable));
		Assert.Equal(Visibility.Visible, editableBox.Visibility);
		Assert.Equal(Visibility.Collapsed,
			Assert.IsType<ContentPresenter>(editable.Template.FindName("contentPresenter", editable)).Visibility);
	}

	[UITheory]
	[MemberData(nameof(Themes))]
	public void BusyIndicator_UsesTheThemeTemplate_AndItsOverlayFollowsIsBusy(string theme)
	{
		ResourceDictionary dictionary = Load(theme);
		Style style = StyleFor(dictionary, typeof(BusyIndicator));

		var indicator = new BusyIndicator { Style = style, BusyContent = "Loading" };
		indicator.Resources.MergedDictionaries.Add(dictionary);
		indicator.ApplyTemplate();
		var overlay = Assert.IsAssignableFrom<UIElement>(indicator.Template.FindName("PART_BusyOverlay", indicator));

		indicator.IsBusy = false;
		indicator.ApplyTemplate();
		Assert.NotEqual(Visibility.Visible, overlay.Visibility);

		indicator.IsBusy = true;
		Assert.Equal(Visibility.Visible, overlay.Visibility);
	}

	[UITheory]
	[MemberData(nameof(Themes))]
	public void ListViewItem_HasAnImplicitStyleForKeyedStylesToBeBasedOn(string theme)
	{
		// SharpGridView's container style is BasedOn the implicit ListViewItem style; the lookup is deferred until the
		// first row is realized, so a missing style surfaced as a XamlParseException and unreadable rows.
		Style style = StyleFor(Load(theme), typeof(ListViewItem));

		Assert.Contains(style.Setters.OfType<Setter>(), s => s.Property == Control.ForegroundProperty);
		Assert.Contains(style.Setters.OfType<Setter>(), s => s.Property == Control.BackgroundProperty);
	}

	[Fact]
	public void EveryLightKeyHasADarkCounterpart()
	{
		// Light is the framework's own look plus the shell's chrome; Dark may add overrides of its own (the property
		// grid editors, inactive selection and tooltip colours) but must restyle everything Light does. A style or
		// brush present only in Light shows up as an unreadable, half-themed control after a switch to Dark.
		var light = Keys("Light");
		var dark = Keys("Dark");

		Assert.Empty(light.Except(dark).OrderBy(k => k));
	}

	static HashSet<string> Keys(string theme)
	{
		XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
		XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
		var keys = new HashSet<string>(StringComparer.Ordinal);
		foreach (XElement child in XDocument.Load(ThemePath(theme)).Root!.Elements()) {
			string? key = (string?)child.Attribute(x + "Key");
			if (key != null) {
				keys.Add(key);
			} else if (child.Name == presentation + "Style") {
				keys.Add("Style:" + (string?)child.Attribute("TargetType"));
			}
		}
		return keys;
	}
}
