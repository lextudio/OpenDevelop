// Copyright (c) 2026 LeXtudio Inc.
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
using System.Collections;
using System.Collections.Specialized;
using ICSharpCode.ILSpyX.TreeView;

namespace ICSharpCode.ILSpy.Controls.TreeView
{
	/// <summary>
	/// Read-only view over a <see cref="TreeFlattener"/> that reports multi-item runs as
	/// <see cref="NotifyCollectionChangedAction.Reset"/> instead of as a ranged Add/Remove. Used as
	/// <see cref="System.Windows.Controls.ItemsControl.ItemsSource"/> in place of the flattener
	/// itself.
	///
	/// WHY: a node plus its visible descendants form one contiguous run in the flattened list, and
	/// the flattener updates its Count for the WHOLE run before raising anything, so the
	/// notification has to describe the run as ONE step - otherwise a consumer that re-indexes
	/// while reconciling reads past the end of an already-resized source. But a multi-item
	/// Add/Remove cannot express that either: WPF's
	/// ListCollectionView.ValidateCollectionChangedEventArgs rejects any Add/Remove whose item
	/// count != 1 with NotSupportedException("Range actions are not supported.") - stock WPF
	/// behaviour, not a LibreWPF quirk. That threw out of SharpTreeNodeCollection.RemoveAll while
	/// the workbench closed a solution, and since the close runs inside the native window-close
	/// callback the escaping exception aborted the process (SIGABRT) rather than surfacing as an
	/// error. Reset is exempt from that validation and still means "re-read everything, the source
	/// is now settled", which is the same one-step invariant. Single-item changes are passed
	/// through unchanged so the common case stays incremental.
	///
	/// This lives on OpenDevelop's side of the boundary on purpose. The same fix used to be a patch
	/// to ILSpyX's own TreeFlattener, which forced ILSpyX (and ICSharpCode.Decompiler with it) to be
	/// consumed as submodule project references instead of the published packages. Wrapping works
	/// because everything needed is public on TreeFlattener, and because this control is the only
	/// place in the repo that constructs one.
	///
	/// It also preserves something the patch destroyed: SharpTreeView's own handler stays
	/// subscribed to the RAW flattener, so its deselection logic still sees precise OldItems for a
	/// multi-node removal, which a Reset cannot carry.
	/// </summary>
	sealed class RangeCollapsingItemsSource : IList, INotifyCollectionChanged
	{
		readonly TreeFlattener flattener;

		public RangeCollapsingItemsSource(TreeFlattener flattener)
		{
			this.flattener = flattener ?? throw new ArgumentNullException(nameof(flattener));
			this.flattener.CollectionChanged += OnFlattenerCollectionChanged;
		}

		/// <summary>Stops forwarding. The flattener itself is stopped by its owner.</summary>
		public void Detach()
		{
			flattener.CollectionChanged -= OnFlattenerCollectionChanged;
		}

		public event NotifyCollectionChangedEventHandler CollectionChanged;

		void OnFlattenerCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			var handler = CollectionChanged;
			if (handler == null)
				return;
			handler(this, IsRangeAction(e)
				? new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)
				: e);
		}

		static bool IsRangeAction(NotifyCollectionChangedEventArgs e)
		{
			switch (e.Action)
			{
				case NotifyCollectionChangedAction.Add:
					return e.NewItems != null && e.NewItems.Count != 1;
				case NotifyCollectionChangedAction.Remove:
					return e.OldItems != null && e.OldItems.Count != 1;
				default:
					return false;
			}
		}

		public object this[int index] {
			get { return flattener[index]; }
			set { throw new NotSupportedException(); }
		}

		public int Count {
			get { return flattener.Count; }
		}

		public int IndexOf(object item)
		{
			return flattener.IndexOf(item);
		}

		public bool Contains(object item)
		{
			return flattener.Contains(item);
		}

		public void CopyTo(Array array, int arrayIndex)
		{
			flattener.CopyTo(array, arrayIndex);
		}

		public IEnumerator GetEnumerator()
		{
			return flattener.GetEnumerator();
		}

		bool IList.IsReadOnly {
			get { return true; }
		}

		bool IList.IsFixedSize {
			get { return false; }
		}

		bool ICollection.IsSynchronized {
			get { return false; }
		}

		object ICollection.SyncRoot {
			get { return this; }
		}

		void IList.Insert(int index, object item)
		{
			throw new NotSupportedException();
		}

		void IList.RemoveAt(int index)
		{
			throw new NotSupportedException();
		}

		int IList.Add(object item)
		{
			throw new NotSupportedException();
		}

		void IList.Clear()
		{
			throw new NotSupportedException();
		}

		void IList.Remove(object item)
		{
			throw new NotSupportedException();
		}
	}
}
