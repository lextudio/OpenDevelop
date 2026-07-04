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
using System.Diagnostics;
using System.Net.Mime;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ICSharpCode.SharpDevelop.Logging
{
	sealed class SDTraceListener : DefaultTraceListener
	{
		[Conditional("DEBUG")]
		public static void Install()
		{
			Trace.Listeners.Clear();
			Trace.Listeners.Add(new SDTraceListener());
		}
		
		public SDTraceListener()
		{
			base.AssertUiEnabled = false;
		}
		
		HashSet<string> ignoredStacks = new HashSet<string>();
		AtomicBoolean dialogIsOpen;
		
		public override void Fail(string message)
		{
			this.Fail(message, null);
		}
		
		public override void Fail(string message, string detailMessage)
		{
			base.Fail(message, detailMessage); // let base class write the assert to the debug console
			string stackTrace = "";
			try {
				stackTrace = new StackTrace(true).ToString();
			} catch {}
			lock (ignoredStacks) {
				if (ignoredStacks.Contains(stackTrace))
					return;
			}
			if (!dialogIsOpen.Set())
				return;
			// We might be unable to display a dialog here, e.g. because
			// we're on the UI thread but dispatcher processing is disabled.
			// In any case, we don't want to pump messages while the dialog is displaying,
			// so we create a separate UI thread for the dialog:
			bool debug = false;
			var thread = new Thread(() => ShowAssertionDialog(message, detailMessage, stackTrace, ref debug));
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			thread.Join();
			if (debug)
				Debugger.Break();
		}
		
		void ShowAssertionDialog(string message, string detailMessage, string stackTrace, ref bool debug)
		{
			// CustomDialog (WinForms multi-button dialog) is out of MVP scope - fall back to a plain WPF
			// message box with Yes(=Debug)/No(=Ignore)/Cancel(=Ignore All) semantics.
			message = message + Environment.NewLine + detailMessage + Environment.NewLine + stackTrace;
			try {
				var result = System.Windows.MessageBox.Show(
					message.TakeStartEllipsis(750) + Environment.NewLine + Environment.NewLine +
					"Yes = Debug, No = Ignore, Cancel = Ignore All",
					"Assertion Failed", MessageBoxButton.YesNoCancel);
				switch (result) {
					case MessageBoxResult.Yes:
						debug = true;
						break;
					case MessageBoxResult.Cancel:
						lock (ignoredStacks) {
							ignoredStacks.Add(stackTrace);
						}
						break;
				}
			} finally {
				dialogIsOpen.Reset();
			}
		}
	}
}
