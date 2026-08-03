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

using System.Linq;

using ICSharpCode.ILSpyX.TreeView;

namespace ICSharpCode.AndroidSdkManager
{
	/// <summary>
	/// A non-leaf grouping node (an API level under Platforms, or a tool family under Tools).
	/// Its own IsChecked is the tri-state aggregate of its children (SharpTreeNode base behaviour).
	/// </summary>
	public class SdkGroupNode : SharpTreeNode
	{
		readonly string text;

		public SdkGroupNode(string text)
		{
			this.text = text;
		}

		public override object Text {
			get { return text; }
		}

		public override bool IsCheckable {
			get { return true; }
		}

		public string VersionText {
			get { return string.Empty; }
		}

		public string Size {
			get { return string.Empty; }
		}

		public string StatusText {
			get {
				var leaves = Descendants().OfType<SdkPackageTreeNode>().ToList();
				if (leaves.Count == 0)
					return string.Empty;
				if (leaves.Any(l => l.Package.HasUpdate))
					return "Update available";
				return string.Empty;
			}
		}
	}

	/// <summary>
	/// A leaf node wrapping one <see cref="SdkPackage"/>; checking it marks it for install,
	/// unchecking an installed package marks it for removal (see PendingAction).
	/// </summary>
	public class SdkPackageTreeNode : SharpTreeNode
	{
		public SdkPackage Package { get; }

		public SdkPackageTreeNode(SdkPackage package)
		{
			Package = package;
			IsChecked = package.IsInstalled;
			// SharpTreeNode.IsChecked's setter isn't virtual, so re-raise StatusText here
			// whenever the checkbox changes (StatusText depends on IsChecked vs Package.IsInstalled).
			PropertyChanged += (s, e) => {
				if (e.PropertyName == "IsChecked")
					RaisePropertyChanged("StatusText");
			};
		}

		public override object Text {
			get { return Package.DisplayName; }
		}

		public override bool IsCheckable {
			get { return true; }
		}

		public string VersionText {
			get { return Package.VersionText; }
		}

		public string Size {
			get { return Package.Size; }
		}

		public string StatusText {
			get {
				if (IsChecked == true && !Package.IsInstalled)
					return "To be installed";
				if (IsChecked == false && Package.IsInstalled)
					return "To be removed";
				return Package.StatusText;
			}
		}

		/// <summary>True if the current checkbox state differs from what's actually installed.</summary>
		public bool IsPendingChange {
			get { return IsChecked.HasValue && IsChecked.Value != Package.IsInstalled; }
		}

		public bool IsPendingInstall {
			get { return IsChecked == true && !Package.IsInstalled; }
		}

		public bool IsPendingRemoval {
			get { return IsChecked == false && Package.IsInstalled; }
		}
	}
}
