// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ICSharpCode.XmlEditor
{
	/// <summary>
	/// WPF-native replacement for the WinForms <c>ExtTreeNode</c> base that
	/// <see cref="XmlElementTreeNode"/>/<see cref="XmlTextTreeNode"/>/<see cref="XmlCommentTreeNode"/>
	/// used to derive from. A plain data node (not a WPF control) bound into
	/// <see cref="XmlTreeViewControl"/>'s <see cref="System.Windows.Controls.TreeView"/> via a
	/// <see cref="System.Windows.HierarchicalDataTemplate"/>, instead of being a WinForms TreeNode itself.
	/// </summary>
	public abstract class XmlTreeNode : INotifyPropertyChanged
	{
		string text = string.Empty;
		bool showGhostImage;
		bool isExpanded;
		bool isSelected;

		public event PropertyChangedEventHandler PropertyChanged;

		public string Text {
			get { return text; }
			set { text = value; OnPropertyChanged(nameof(Text)); }
		}

		public object Tag { get; set; }

		public ObservableCollection<XmlTreeNode> Nodes { get; } = new ObservableCollection<XmlTreeNode>();

		public XmlTreeNode Parent { get; private set; }

		internal XmlTreeViewControl OwnerTreeView { get; private set; }

		public virtual bool ShowGhostImage {
			get { return showGhostImage; }
			set { showGhostImage = value; OnPropertyChanged(nameof(ShowGhostImage)); }
		}

		public bool IsExpanded {
			get { return isExpanded; }
			set { isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
		}

		public bool IsSelected {
			get { return isSelected; }
			set { isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
		}

		/// <summary>Adds this node as a child of <paramref name="parent"/>.</summary>
		public void AddTo(XmlTreeNode parent)
		{
			Parent = parent;
			parent.Nodes.Add(this);
		}

		/// <summary>Adds this node as a root node of <paramref name="treeView"/>.</summary>
		public void AddTo(XmlTreeViewControl treeView)
		{
			OwnerTreeView = treeView;
			treeView.Nodes.Add(this);
		}

		/// <summary>Inserts this node as a child of <paramref name="parent"/> at <paramref name="index"/>.</summary>
		public void Insert(int index, XmlTreeNode parent)
		{
			Parent = parent;
			parent.Nodes.Insert(index, this);
		}

		/// <summary>Inserts this node as a root node of <paramref name="treeView"/> at <paramref name="index"/>.</summary>
		public void Insert(int index, XmlTreeViewControl treeView)
		{
			OwnerTreeView = treeView;
			treeView.Nodes.Insert(index, this);
		}

		public void Remove()
		{
			if (Parent != null) {
				Parent.Nodes.Remove(this);
				Parent = null;
			} else if (OwnerTreeView != null) {
				OwnerTreeView.Nodes.Remove(this);
				OwnerTreeView = null;
			}
		}

		public void Expand()
		{
			IsExpanded = true;
		}

		protected void OnPropertyChanged(string propertyName)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}
