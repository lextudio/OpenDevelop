using System;
using System.Windows;
using System.Windows.Controls;

namespace ICSharpCode.IconEditor
{
	public partial class PickFormatDialog : Window
	{
		int[] colorDepths = { 1, 4, 8, 24, 32 };

		public PickFormatDialog()
		{
			InitializeComponent();
		}

		public int IconWidth
		{
			get
			{
				int value;
				int.TryParse(widthTextBox.Text, out value);
				return Math.Max(1, Math.Min(256, value));
			}
		}

		public int IconHeight
		{
			get
			{
				int value;
				int.TryParse(heightTextBox.Text, out value);
				return Math.Max(1, Math.Min(256, value));
			}
		}

		public int ColorDepth
		{
			get { return colorDepths[Math.Max(0, colorDepthComboBox.SelectedIndex)]; }
		}

		void OkButtonClick(object sender, RoutedEventArgs e)
		{
			DialogResult = true;
			Close();
		}

		void CancelButtonClick(object sender, RoutedEventArgs e)
		{
			DialogResult = false;
			Close();
		}
	}
}
