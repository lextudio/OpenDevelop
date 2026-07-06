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
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;

using Debugger.AddIn.Service.Dap;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Services;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	public class ThreadsPad : AbstractPadContent
	{
		ListView listView;

		public override object Control {
			get { return listView; }
		}

		public ThreadsPad()
		{
			var res = new CommonResources();
			res.InitializeComponent();

			listView = new ListView();
			listView.View = (GridView)res["threadsGridView"];
			listView.MouseDoubleClick += listView_MouseDoubleClick;
			listView.SetValue(GridViewColumnAutoSize.AutoWidthProperty, "70;100%;75;75");

			WindowsDebugger.RefreshingPads += RefreshPad;
			RefreshPad();
		}

		async void RefreshPad()
		{
			await RefreshPadAsync().ConfigureAwait(true);
		}

		async Task<IReadOnlyList<ThreadItem>> RefreshPadAsync()
		{
			var session = WindowsDebugger.CurrentSession;
			if (session == null || !session.IsPaused) {
				listView.ItemsSource = null;
				return Array.Empty<ThreadItem>();
			}

			var threads = await session.GetThreadsAsync().ConfigureAwait(true);
			var items = new List<ThreadItem>();
			foreach (var thread in threads) {
				items.Add(new ThreadItem(thread));
			}
			listView.ItemsSource = items;
			return items;
		}

		/// <summary>Used by the DevFlow "od.debug.pad-snapshot" test action.</summary>
		public async Task<IEnumerable<object>> GetSnapshotAsync()
		{
			var items = await RefreshPadAsync().ConfigureAwait(true);
			return items.Select(i => (object)new { i.ID, i.Name });
		}

		void listView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			ThreadItem item = listView.SelectedItem as ThreadItem;
			if (item == null)
				return;

			var session = WindowsDebugger.CurrentSession;
			if (session != null) {
				if (session.IsPaused) {
					WindowsDebugger.CurrentThread = item.Thread;
					WindowsDebugger.Instance.JumpToCurrentLine();
					WindowsDebugger.RefreshPads();
				} else {
					MessageService.ShowMessage("${res:MainWindow.Windows.Debug.Threads.CannotSwitchWhileRunning}", "${res:MainWindow.Windows.Debug.Threads.ThreadSwitch}");
				}
			}
		}
	}

	public class ThreadItem
	{
		public DapThreadInfo Thread { get; private set; }
		public int ID { get; private set; }
		public string Name { get; private set; }

		// DAP has no standard equivalent of ICorDebug's thread priority/suspend-count, so the
		// "Priority"/"Frozen" columns (and the old Freeze context menu) are left blank. Known gap.
		public string Priority { get { return string.Empty; } }
		public string Frozen { get { return string.Empty; } }

		public ThreadItem(DapThreadInfo thread)
		{
			this.Thread = thread;
			this.ID = thread.Id;
			this.Name = thread.Name;
		}
	}
}
