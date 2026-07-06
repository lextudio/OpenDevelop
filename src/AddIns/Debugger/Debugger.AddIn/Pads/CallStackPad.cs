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
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Debugger.AddIn.Service.Dap;
using ICSharpCode.Core.Presentation;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Services;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui.Pads
{
	public class CallStackPad : AbstractPadContent
	{
		ListView listView;

		public override object Control {
			get { return this.listView; }
		}

		public CallStackPad()
		{
			var res = new CommonResources();
			res.InitializeComponent();

			listView = new ListView();
			listView.View = (GridView)res["callstackGridView"];
			listView.MouseDoubleClick += listView_MouseDoubleClick;
			listView.SetValue(GridViewColumnAutoSize.AutoWidthProperty, "100%");

			WindowsDebugger.RefreshingPads += RefreshPad;
			RefreshPad();
		}

		void listView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			CallStackItem item = listView.SelectedItem as CallStackItem;
			if (item == null || item.Frame == null)
				return;

			if (WindowsDebugger.CurrentSession != null && WindowsDebugger.CurrentSession.IsPaused) {
				WindowsDebugger.CurrentStackFrame = item.Frame;
				WindowsDebugger.CurrentSession.ActiveFrameId = item.Frame.Id;
				WindowsDebugger.Instance.JumpToCurrentLine();
				WindowsDebugger.RefreshPads();
			} else {
				MessageService.ShowMessage("${res:MainWindow.Windows.Debug.CallStack.CannotSwitchWhileRunning}", "${res:MainWindow.Windows.Debug.CallStack.FunctionSwitch}");
			}
		}

		async void RefreshPad()
		{
			var session = WindowsDebugger.CurrentSession;
			var thread = WindowsDebugger.CurrentThread;
			if (session == null || thread == null || !session.IsPaused) {
				listView.ItemsSource = null;
				return;
			}

			var frames = await session.GetStackFramesAsync(thread.Id).ConfigureAwait(true);
			var items = new ObservableCollection<CallStackItem>();
			foreach (var frame in frames) {
				items.Add(new CallStackItem {
					Frame = frame,
					ImageSource = SD.ResourceService.GetImageSource("Icons.16x16.Method"),
					Name = frame.Name + (frame.Line > 0 ? ":" + frame.Line : string.Empty),
					HasSymbols = !string.IsNullOrEmpty(frame.FilePath)
				});
			}
			listView.ItemsSource = items;
		}
	}

	public class CallStackItem
	{
		public DapStackFrameInfo Frame { get; set; }
		public ImageSource ImageSource { get; set; }
		public string Name { get; set; }
		public bool HasSymbols { get; set; }

		public Brush FontColor {
			get { return this.HasSymbols ? Brushes.Black : Brushes.Gray; }
		}
	}
}
