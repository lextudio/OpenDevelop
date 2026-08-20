// Shared visual-designer canvas shell. Every designer backend (WinForms, WPF, WinUI/Uno) hosts
// its rendered design surface in this control so the surrounding chrome - the toolbar (zoom,
// design-size preset, fit, gridlines, design theme), the empty-canvas edge pattern, and the
// toolbar theme - looks and behaves identically across all three. The backend-specific surface
// (frame + selection + gestures) goes into <see cref="ContentHost"/>.
//
// Two themes are in play:
//  - the IDE theme (ApplyIdeTheme) drives the empty-canvas edge color around the design bitmap;
//  - the design theme (ApplyDesignTheme) drives the toolbar chrome (the designer can render its
//    page Light or Dark independently of the IDE).
//
// The design theme is chosen from a combo box that starts with the universal Light/Dark pair
// and can be replaced by the backend with the themes the design actually carries - for WinUI/Uno
// that is the set of ResourceDictionary.ThemeDictionaries keys hoisted from the app's App.xaml
// (see AppResourceBuilder), so the designer's theme list always mirrors the app under design
// instead of hardcoding two entries.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

using ICSharpCode.Core.Presentation;

namespace ICSharpCode.SharpDevelop.Widgets
{
	public class DesignerCanvas : ContentControl
	{
		readonly Grid root = new Grid();
		readonly StackPanel toolbar = new StackPanel { Orientation = Orientation.Horizontal };
		readonly Button fitButton;
		readonly ToggleButton gridButton;
		readonly ComboBox themeCombo;
		readonly ToggleButton namesButton;
		bool syncingTheme;

		public DesignerCanvas()
		{
			fitButton = CreateIconButton("Icons.16x16.FitToScreen", "Fit the design to the surface");
			gridButton = CreateIconToggle("Icons.16x16.GridGuide", "Show design-space gridlines");
			themeCombo = new ComboBox {
				Width = 84,
				Margin = new Thickness(0, 2, 4, 2),
				ToolTip = "Switch the design surface theme"
			};
			themeCombo.Items.Add("Light");
			themeCombo.Items.Add("Dark");
			themeCombo.SelectedIndex = 0;
			namesButton = CreateIconToggle("Icons.16x16.DisplayName", "Show control names on the selection outline");

			toolbar.Children.Add(ZoomCombo);
			toolbar.Children.Add(fitButton);
			toolbar.Children.Add(gridButton);
			toolbar.Children.Add(themeCombo);
			toolbar.Children.Add(namesButton);
			// The design-size preset combo sits on its own at the far right.
			toolbar.Children.Add(DesignSizeCombo);

			root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			Grid.SetRow(toolbar, 0);
			Grid.SetRow(ContentHost, 1);
			root.Children.Add(toolbar);
			root.Children.Add(ContentHost);
			Content = root;

			ZoomCombo.SelectionChanged += (_, _) => ZoomChanged?.Invoke(this, EventArgs.Empty);
			DesignSizeCombo.SelectionChanged += (_, e) => {
				if (e.AddedItems.Count > 0 && e.AddedItems[0] is string label && DesignSizeCombo.SelectedIndex > 0)
					DesignSizeSelected?.Invoke(this, label);
			};
			fitButton.Click += (_, _) => FitRequested?.Invoke(this, EventArgs.Empty);
			// Checked/Unchecked must also refresh the button's OWN highlight: ApplyDesignTheme
			// only runs once at construction (and whenever the IDE/design theme switches), so
			// without this a real click toggled the underlying behavior correctly but the button
			// never looked pressed - which is exactly why "the grid button doesn't work" was
			// reported: SetGridlines(true) WAS running, the button just never lit up to show it.
			gridButton.Checked += (_, _) => { UpdateButtonHighlight(gridButton); GridRequested?.Invoke(this, true); };
			gridButton.Unchecked += (_, _) => { UpdateButtonHighlight(gridButton); GridRequested?.Invoke(this, false); };
			themeCombo.SelectionChanged += (_, _) => {
				if (syncingTheme)
					return;
				if (themeCombo.SelectedItem is string theme)
					ThemeRequested?.Invoke(this, theme);
			};
			namesButton.Checked += (_, _) => { UpdateButtonHighlight(namesButton); ShowNamesRequested?.Invoke(this, true); };
			namesButton.Unchecked += (_, _) => { UpdateButtonHighlight(namesButton); ShowNamesRequested?.Invoke(this, false); };

			ShowZoom = true;
			ShowDesignSize = true;
			ShowFit = true;
			ShowGrid = true;
			ShowTheme = true;
			ShowNames = true;
			// Pressed = show names, matching today's existing behavior by default.
			IsShowingNames = true;
			// The toolbar chrome follows the IDE theme (not the design theme): toolbar background
			// and the combo/button text use the semantic ToolWindowBackground/Foreground keys so
			// they switch with the IDE. The design theme only drives the checked-button highlight
			// below (ApplyDesignTheme).
			toolbar.SetResourceReference(Panel.BackgroundProperty, "ToolWindowBackground");
			ZoomCombo.SetResourceReference(Control.ForegroundProperty, "Foreground");
			DesignSizeCombo.SetResourceReference(Control.ForegroundProperty, "Foreground");
			fitButton.SetResourceReference(Control.ForegroundProperty, "Foreground");
			gridButton.SetResourceReference(Control.ForegroundProperty, "Foreground");
			themeCombo.SetResourceReference(Control.ForegroundProperty, "Foreground");
			namesButton.SetResourceReference(Control.ForegroundProperty, "Foreground");
			ApplyDesignTheme(false);
			// The empty-canvas edge follows the IDE theme via the semantic theme's "EdgePattern"
			// key (Themes/Theme.Light.xaml / Theme.Dark.xaml each define their own), so a theme
			// switch is picked up automatically by the DynamicResource.
			SetResourceReference(BackgroundProperty, "EdgePattern");
		}

		/// <summary>Where the backend mounts its rendered surface (frame + selection + gestures).</summary>
		public ContentControl ContentHost { get; } = new ContentControl();

		/// <summary>Zoom preset labels ("Fit", "100%", ...). Index 0 is "Fit".</summary>
		public ComboBox ZoomCombo { get; } = new ComboBox { Width = 84, Margin = new Thickness(4, 2, 4, 2) };

		/// <summary>Design-size preset labels ("Auto", "Phone 390x844", ...).</summary>
		public ComboBox DesignSizeCombo { get; } = new ComboBox {
			Width = 150,
			Margin = new Thickness(0, 2, 4, 2),
			ToolTip = "Design canvas size preset (for pages without an explicit size)"
		};

		public bool ShowZoom { get { return ZoomCombo.Visibility == Visibility.Visible; } set { ZoomCombo.Visibility = value ? Visibility.Visible : Visibility.Collapsed; } }
		public bool ShowDesignSize { get { return DesignSizeCombo.Visibility == Visibility.Visible; } set { DesignSizeCombo.Visibility = value ? Visibility.Visible : Visibility.Collapsed; } }
		public bool ShowFit { get { return fitButton.Visibility == Visibility.Visible; } set { fitButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; } }
		public bool ShowGrid { get { return gridButton.Visibility == Visibility.Visible; } set { gridButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; } }
		public bool ShowTheme { get { return themeCombo.Visibility == Visibility.Visible; } set { themeCombo.Visibility = value ? Visibility.Visible : Visibility.Collapsed; } }
		public bool ShowNames { get { return namesButton.Visibility == Visibility.Visible; } set { namesButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; } }

		/// <summary>Gridlines toggle state (checked = show grid).</summary>
		public bool IsGridEnabled { get { return gridButton.IsChecked == true; } set { gridButton.IsChecked = value; } }

		/// <summary>Currently selected design-theme name.</summary>
		public string DesignTheme
		{
			get => themeCombo.SelectedItem as string ?? "Light";
			set
			{
				syncingTheme = true;
				var index = themeCombo.Items.IndexOf(value);
				themeCombo.SelectedIndex = index >= 0 ? index : 0;
				syncingTheme = false;
			}
		}

		/// <summary>Design-theme state as a boolean: true = Dark. Read for state inspection;
		/// write only to sync the combo from outside (does not raise <see cref="ThemeRequested"/>).</summary>
		public bool IsDarkTheme
		{
			get => string.Equals(DesignTheme, "Dark", StringComparison.OrdinalIgnoreCase);
			set => DesignTheme = value ? "Dark" : "Light";
		}

		/// <summary>Replaces the theme list with the themes the design actually carries (for
		/// WinUI/Uno: the app's ResourceDictionary.ThemeDictionaries keys). Keeps the current
		/// selection when the new list still contains it.</summary>
		public void SetDesignThemes(IEnumerable<string> themes)
		{
			var current = DesignTheme;
			syncingTheme = true;
			themeCombo.Items.Clear();
			foreach (var theme in themes)
			{
				themeCombo.Items.Add(theme);
			}
			var index = themeCombo.Items.IndexOf(current);
			themeCombo.SelectedIndex = index >= 0 ? index : 0;
			syncingTheme = false;
		}

		/// <summary>Selection-name-label toggle state: pressed (checked) shows the control name
		/// above every selection outline (today's existing behavior, the default); released
		/// (unchecked) shows only the selection outline itself.</summary>
		public bool IsShowingNames { get { return namesButton.IsChecked == true; } set { namesButton.IsChecked = value; } }

		public event EventHandler ZoomChanged;
		public event EventHandler<string> DesignSizeSelected;
		public event EventHandler FitRequested;
		public event EventHandler<bool> GridRequested;
		public event EventHandler<string> ThemeRequested;
		public event EventHandler<bool> ShowNamesRequested;

		/// <summary>Switches the checked-button highlight between the Light and Dark design
		/// themes. The toolbar background/text follow the IDE theme (DynamicResource), so only
		/// the checked (active) button state is design-theme-dependent here.</summary>
		public void ApplyDesignTheme(bool dark)
		{
			UpdateButtonHighlight(gridButton);
			UpdateButtonHighlight(namesButton);
		}

		static void UpdateButtonHighlight(ToggleButton button) =>
			button.Background = button.IsChecked == true ? CheckedBackground : null;

		static Button CreateIconButton(string iconKey, string toolTip)
		{
			var button = new Button {
				Content = CreateIcon(iconKey),
				Margin = new Thickness(0, 2, 4, 2),
				Padding = new Thickness(4, 2, 4, 2),
				ToolTip = toolTip
			};
			return button;
		}

		static ToggleButton CreateIconToggle(string iconKey, string toolTip)
		{
			var button = new ToggleButton {
				Content = CreateIcon(iconKey),
				Margin = new Thickness(0, 2, 4, 2),
				Padding = new Thickness(4, 2, 4, 2),
				ToolTip = toolTip
			};
			return button;
		}

		static Image CreateIcon(string iconKey)
		{
			var image = new Image { Width = 16, Height = 16, Stretch = Stretch.Uniform };
			try {
				image.Source = PresentationResourceService.GetImageSource(iconKey);
			} catch {
				image.Source = null;
			}
			return image;
		}

		static readonly Brush CheckedBackground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));

	}
}
