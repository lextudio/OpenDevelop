using System;
using System.Collections.Specialized;
using System.Windows;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.TreeView;

namespace ICSharpCode.UnitTesting
{
	public class UnitTestNode : SharpTreeNode
	{
		readonly ITest test;

		public UnitTestNode(ITest test)
		{
			if (test == null)
				throw new ArgumentNullException("test");
			this.test = test;
			this.LazyLoading = true;
			if (IsVisible) {
				test.DisplayNameChanged += test_NameChanged;
				test.ResultChanged += test_ResultChanged;
			}
		}

		protected override void OnIsVisibleChanged()
		{
			base.OnIsVisibleChanged();
			if (IsVisible) {
				test.DisplayNameChanged += test_NameChanged;
				test.ResultChanged += test_ResultChanged;
			} else {
				test.DisplayNameChanged -= test_NameChanged;
				test.ResultChanged -= test_ResultChanged;
			}
		}

		public new ITest Model {
			get { return test; }
		}

		protected override void LoadChildren()
		{
			foreach (var nested in test.NestedTests) {
				Children.Add(new UnitTestNode(nested));
			}
		}

		public override void ActivateItem(RoutedEventArgs e)
		{
			if (test.GoToDefinition.CanExecute(e))
				test.GoToDefinition.Execute(e);
		}

		public override bool ShowExpander {
			get { return test.CanExpandNestedTests && base.ShowExpander; }
		}

		public override bool CanExpandRecursively {
			get { return true; }
		}

		public override object Icon {
			get {
				switch (test.Result) {
					case TestResultType.None:
						return Images.Grey;
					case TestResultType.Success:
						return Images.Green;
					case TestResultType.Failure:
						return Images.Red;
					case TestResultType.Ignored:
						return Images.Yellow;
					default:
						throw new NotSupportedException("Invalid value for TestResultType");
				}
			}
		}

		void test_ResultChanged(object sender, EventArgs e)
		{
			RaisePropertyChanged("Icon");
			RaisePropertyChanged("ExpandedIcon");
		}

		public override object Text {
			get { return test.DisplayName; }
		}

		void test_NameChanged(object sender, EventArgs e)
		{
			RaisePropertyChanged("Text");
		}
	}
}
