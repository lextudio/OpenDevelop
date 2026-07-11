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

using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using ICSharpCode.SharpDevelop;
using ICSharpCode.TreeView;

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// WPF replacement for the old WinForms-based ExtTreeNode-derived class (ExtTreeNode/ExtTreeView
	/// no longer exist in this fork's ICSharpCode.Core.WinForms) - rebased onto
	/// ICSharpCode.TreeView.SharpTreeNode, the same WPF tree-node base UnitTesting's TestTreeView/
	/// UnitTestNode already use. See that pair for the pattern this mirrors.
	///
	/// One behavioral note vs. the old code: WinForms' ExtTreeNode lazily built children on first
	/// AfterSelect (via a dummy child node + Initialize()/PerformInitialization()). SharpTreeNode
	/// has its own, equivalent lazy-loading mechanism (LazyLoading + LoadChildren()), triggered on
	/// first expand rather than first select - callers that need children populated at selection
	/// time (not just expansion time) should call EnsureLazyChildren() explicitly, the same way
	/// UnitTestNode.FindNode() already does elsewhere in this codebase.
	/// </summary>
	public class CodeCoverageTreeNode : SharpTreeNode
	{
		/// <summary>Code coverage is less than one hundred percent.</summary>
		public static readonly Brush PartialCoverageTextBrush = Brushes.Red;

		/// <summary>Code coverage is 100% but branch coverage is not 0% (no branches present) or 100% (all branches covered).</summary>
		public static readonly Brush PartialBranchesTextBrush = Brushes.DarkGreen;

		/// <summary>Code coverage is zero.</summary>
		public static readonly Brush ZeroCoverageTextBrush = Brushes.Gray;

		string name;
		int visitedCodeLength;
		int unvisitedCodeLength;
		decimal visitedBranchCoverage;
		CodeCoverageImageListIndex imageIndex;

		public CodeCoverageTreeNode(string name, CodeCoverageImageListIndex index)
			: this(name, index, 0, 0, 0)
		{
		}

		public CodeCoverageTreeNode(ICodeCoverageWithVisits codeCoverageWithVisits, CodeCoverageImageListIndex index)
			: this(codeCoverageWithVisits.Name,
				index,
				codeCoverageWithVisits.GetVisitedCodeLength(),
				codeCoverageWithVisits.GetUnvisitedCodeLength(),
				codeCoverageWithVisits.GetVisitedBranchCoverage()
			)
		{
		}

		public CodeCoverageTreeNode(string name, CodeCoverageImageListIndex index, int visitedCodeLength, int unvisitedCodeLength, decimal visitedBranchCoverage = 100)
		{
			SortOrder = 10;
			this.name = name;
			this.imageIndex = index;
			this.visitedCodeLength = visitedCodeLength;
			this.unvisitedCodeLength = unvisitedCodeLength;
			this.visitedBranchCoverage = visitedBranchCoverage;
		}

		/// <summary>
		/// Relative ordering used when sorting sibling nodes (lower sorts first) - replaces the old
		/// ExtTreeNode.sortOrder field. Namespace nodes use 1, everything else defaults to 10, so
		/// namespaces sort before classes/methods within the same parent, matching the old behavior.
		/// </summary>
		public int SortOrder { get; protected set; }

		/// <summary>The plain, unformatted name (Text below formats this with a coverage percentage suffix).</summary>
		public string Name {
			get { return name; }
		}

		public override object Text {
			get { return GetNodeText(); }
		}

		public override Brush Foreground {
			get {
				if (visitedCodeLength == 0) {
					return ZeroCoverageTextBrush;
				} else if (TotalCodeLength != visitedCodeLength) {
					return PartialCoverageTextBrush;
				} else if (TotalCodeLength == visitedCodeLength && VisitedBranchCoverage != 0 && VisitedBranchCoverage != 100) {
					return PartialBranchesTextBrush;
				}
				return base.Foreground;
			}
		}

		public override object Icon {
			get { return CodeCoverageImages.GetImage(imageIndex); }
		}

		string GetNodeText()
		{
			if (TotalCodeLength > 0) {
				if (visitedCodeLength == TotalCodeLength && visitedBranchCoverage != 0 && visitedBranchCoverage != 100) {
					return String.Format("{0} (100%/{1}%)", name, decimal.Round(visitedBranchCoverage, 2));
				}
				int percentage = GetPercentage();
				return String.Format("{0} ({1}%)", name, percentage);
			}
			return name;
		}

		int GetPercentage()
		{
			return TotalCodeLength == 0 ? 0 : (int)decimal.Round((((decimal)visitedCodeLength * 100) / (decimal)TotalCodeLength), 0);
		}

		public int VisitedCodeLength {
			get { return visitedCodeLength; }
			set {
				visitedCodeLength = value;
				RaisePropertyChanged("Text");
				RaisePropertyChanged("Foreground");
				RaisePropertyChanged("Icon");
			}
		}

		public int UnvisitedCodeLength {
			get { return unvisitedCodeLength; }
			set {
				unvisitedCodeLength = value;
				RaisePropertyChanged("Text");
			}
		}

		public int TotalCodeLength {
			get { return visitedCodeLength + unvisitedCodeLength; }
		}

		public decimal VisitedBranchCoverage {
			get { return visitedBranchCoverage; }
			set {
				visitedBranchCoverage = value;
				RaisePropertyChanged("Text");
			}
		}

		/// <summary>
		/// Gets the string to use when sorting the code coverage tree node.
		/// </summary>
		public virtual string CompareString {
			get { return name; }
		}

		/// <summary>
		/// Sorts the child nodes of this node. This sort is not recursive so it only sorts the
		/// immediate children. Replaces ExtTreeView.SortNodes(Nodes, false).
		/// </summary>
		protected void SortChildNodes()
		{
			var sorted = Children
				.OfType<CodeCoverageTreeNode>()
				.OrderBy(n => n.SortOrder)
				.ThenBy(n => n.CompareString, StringComparer.OrdinalIgnoreCase)
				.ToList();
			Children.Clear();
			foreach (var node in sorted) {
				Children.Add(node);
			}
		}

		protected void OpenFile(string fileName)
		{
			if (FileExists(fileName)) {
				FileService.OpenFile(fileName);
			}
		}

		bool FileExists(string fileName)
		{
			return !String.IsNullOrEmpty(fileName) && File.Exists(fileName);
		}

		protected void JumpToFilePosition(string fileName, int line, int column)
		{
			if (FileExists(fileName)) {
				FileService.JumpToFilePosition(fileName, line, column);
			}
		}
	}
}
