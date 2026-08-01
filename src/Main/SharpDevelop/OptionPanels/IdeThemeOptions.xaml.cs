using System.Linq;
using System.Windows.Controls;

using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.OptionPanels
{
	partial class IdeThemeOptions : OptionPanel
	{
		public IdeThemeOptions()
		{
			InitializeComponent();
			themeComboBox.SelectedItem = themeComboBox.Items.OfType<ComboBoxItem>()
				.FirstOrDefault(item => (string)item.Tag == IdeThemeService.CurrentTheme)
				?? themeComboBox.Items[0];
		}

		public override bool SaveOptions()
		{
			var selected = themeComboBox.SelectedItem as ComboBoxItem;
			IdeThemeService.SetTheme(selected?.Tag as string);
			return base.SaveOptions();
		}
	}
}
