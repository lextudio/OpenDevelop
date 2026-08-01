using System;
using System.Collections.Generic;
using System.Windows;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.TreeView;

namespace ICSharpCode.UnitTesting
{
	public class UnitTestNode : ModelCollectionTreeNode
	{
		readonly ITest test;

		public UnitTestNode(ITest test)
		{
			if (test == null)
				throw new ArgumentNullException("test");
			this.test = test;
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

		protected override IModelCollection<object> ModelChildren {
			get { return test.NestedTests; }
		}

		protected override IComparer<SharpTreeNode> NodeComparer {
			get { return NodeTextComparer; }
		}

		protected override object GetModel()
		{
			return test;
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
						return Images.NotRun;
					case TestResultType.Success:
						return Images.Passed;
					case TestResultType.Failure:
						return Images.Failed;
					case TestResultType.Ignored:
						return Images.Skipped;
					default:
						throw new NotSupportedException("Invalid value for TestResultType");
				}
			}
		}

		void test_ResultChanged(object sender, EventArgs e)
		{
			SD.MainThread.InvokeIfRequired(() => {
				RaisePropertyChanged("Icon");
				RaisePropertyChanged("ExpandedIcon");
			});
		}

		public override object Text {
			get { return test.DisplayName; }
		}

		void test_NameChanged(object sender, EventArgs e)
		{
			SD.MainThread.InvokeIfRequired(() => RaisePropertyChanged("Text"));
		}
	}
}
