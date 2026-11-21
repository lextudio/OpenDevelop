using ICSharpCode.Core;
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

		/// <summary>
		/// Recomputes the tri-state from the leaves: all checked, all unchecked, or mixed (null).
		/// SharpTreeNode only cascades a value that is assigned - it never aggregates on its own - so without this a
		/// freshly built group stays null, which the checkbox draws as the indeterminate box (a solid square).
		/// Assigning true or false cascades to the children, which is the same value they already hold here.
		/// </summary>
		public void RefreshCheckState()
		{
			var states = Descendants().OfType<SdkPackageTreeNode>().Select(leaf => leaf.IsChecked).ToList();
			if (states.Count == 0) {
				// An empty group has nothing to be partial about; unchecked is the honest state, and null would draw the
				// indeterminate marker (the solid square this method exists to prevent).
				IsChecked = false;
				return;
			}
			IsChecked = states.All(state => state == true) ? true
				: states.All(state => state == false) ? false
				: (bool?)null;
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
					return ResourceService.GetString("AndroidSdkManager.UpdateAvailable");
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
				if (e.PropertyName != "IsChecked")
					return;
				RaisePropertyChanged("StatusText");
				// Keep the owning group's tri-state in step with the leaves the user ticked. Skip the echo of the group's
				// own cascade: setting a group's IsChecked pushes the value it already holds into every leaf, and
				// recomputing from those half-updated leaves would overwrite the group mid-cascade.
				for (var ancestor = Parent; ancestor is SharpTreeNode; ancestor = ancestor.Parent) {
					if (ancestor is SdkGroupNode owner) {
						if (!(owner.IsChecked is bool groupState && groupState == IsChecked))
							owner.RefreshCheckState();
						break;
					}
				}
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
					return ResourceService.GetString("AndroidSdkManager.ToBeInstalled");
				if (IsChecked == false && Package.IsInstalled)
					return ResourceService.GetString("AndroidSdkManager.ToBeRemoved");
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
