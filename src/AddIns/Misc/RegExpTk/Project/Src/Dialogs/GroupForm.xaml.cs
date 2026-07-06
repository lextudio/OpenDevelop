using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;

namespace Plugins.RegExpTk
{
	public partial class GroupForm : Window
	{
		public GroupForm(Match match)
		{
			InitializeComponent();
			var items = new ObservableCollection<GroupViewModel>();
			foreach (Group g in match.Groups)
				items.Add(new GroupViewModel(g));
			groupsListView.ItemsSource = items;
		}

		void CloseButton_Click(object sender, RoutedEventArgs e)
		{
			Close();
		}
	}

	public class GroupViewModel
	{
		public Group Group { get; }
		public string Value => Group.Value;
		public int Index => Group.Index;
		public int End => Group.Index + Group.Length;
		public int Length => Group.Length;

		public GroupViewModel(Group group)
		{
			Group = group;
		}
	}
}
