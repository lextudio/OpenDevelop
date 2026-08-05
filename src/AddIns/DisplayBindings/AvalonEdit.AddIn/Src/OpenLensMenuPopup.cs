using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// A small context-menu-style popup anchored to a lens row, showing the items of an
	/// <see cref="OpenLensMenu"/> a provider attached to its command (doc/technotes/openlens.md
	/// §20 Phase 4: the test lens's Run/Debug menu). An item with an icon renders icon-only (the
	/// title becomes its tooltip); one without an icon renders its title text. Clicking an item
	/// closes the popup and invokes that item's action; Escape closes without invoking.
	/// </summary>
	sealed class OpenLensMenuPopup : Popup
	{
		public OpenLensMenuPopup(UIElement placementTarget, OpenLensMenu menu)
		{
			PlacementTarget = placementTarget;
			Placement = PlacementMode.Bottom;
			StaysOpen = false;
			AllowsTransparency = true;
			PopupAnimation = PopupAnimation.Fade;

			var panel = new StackPanel {
				Background = SystemColors.ControlBrush,
				MinWidth = 140,
			};
			foreach (var item in menu.Items) {
				var button = new Button {
					Margin = new Thickness(2),
					HorizontalAlignment = HorizontalAlignment.Stretch,
					HorizontalContentAlignment = HorizontalAlignment.Center,
					ToolTip = item.Title,
				};
				var icon = LoadIcon(item.IconKey);
				if (icon != null)
					button.Content = icon;
				else
					button.Content = item.Title;
				button.Click += (sender, e) => {
					IsOpen = false;
					item.Action();
				};
				panel.Children.Add(button);
			}

			Child = new Border {
				BorderBrush = SystemColors.ActiveBorderBrush,
				BorderThickness = new Thickness(1),
				Child = panel,
			};

			Opened += (sender, e) => {
				if (panel.Children.Count > 0 && panel.Children[0] is IInputElement first)
					Keyboard.Focus(first);
			};
			KeyDown += (sender, e) => {
				if (e.Key == Key.Escape) {
					IsOpen = false;
					e.Handled = true;
				}
			};
		}

		static Image LoadIcon(string iconKey)
		{
			if (string.IsNullOrEmpty(iconKey))
				return null;
			try {
				var icon = new Image {
					Source = PresentationResourceService.GetImageSource(iconKey),
					Width = 16,
					Height = 16,
				};
				return icon.Source == null ? null : icon;
			} catch (Exception ex) {
				LoggingService.Warn("OpenLens: couldn't load menu icon '" + iconKey + "'. " + ex.Message);
				return null;
			}
		}
	}
}
