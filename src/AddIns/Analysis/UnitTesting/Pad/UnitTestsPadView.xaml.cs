using System.Windows.Controls;

namespace ICSharpCode.UnitTesting
{
	internal partial class UnitTestsPadView : UserControl
	{
		public UnitTestsPadView()
		{
			InitializeComponent();
		}

		public TestTreeView TreeView => treeView;
		public Grid TreeHost => treeHost;
		public object Toolbar {
			set => toolbarHost.Content = value;
		}
	}
}
