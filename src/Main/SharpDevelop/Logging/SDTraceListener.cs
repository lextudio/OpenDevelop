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
		
		/// <summary>
		/// Opt back in to the old blocking "Assertion Failed" dialog by setting
		/// OPENDEVELOP_ASSERT_DIALOG=1. Off by default - see <see cref="Fail(string, string)"/>.
		/// </summary>
		static readonly bool ShowBlockingDialog =
			Environment.GetEnvironmentVariable("OPENDEVELOP_ASSERT_DIALOG") == "1";

		public override void Fail(string message, string detailMessage)
		{
			base.Fail(message, detailMessage); // let base class write the assert to the debug console
			string stackTrace = "";
			try {
				stackTrace = new StackTrace(true).ToString();
			} catch {}
			lock (ignoredStacks) {
				if (!ignoredStacks.Add(stackTrace))
					return; // already reported this exact assert once
			}

			// Log-and-continue instead of blocking (2026-08-03). This listener is Debug-build-only
			// (see Install's [Conditional("DEBUG")]) and used to spin up a dialog thread and
			// thread.Join() it - i.e. a hard block until a human clicked a button. That is actively
			// hostile in this codebase: we link large amounts of third-party source (ILSpy's
			// decompiler in particular - ICSharpCode.Decompiler.CSharp.SequencePointBuilder,
			// TypeSystem.Implementation.NullabilityAnnotatedType, ...) which is full of
			// Debug.Assert calls that fire on inputs upstream considers non-fatal. Every one of
			// those froze the entire IDE on the UI thread, and because DevFlow actions dispatch to
			// the UI thread, it deadlocked all automation/integration tests too, with no output -
			// making unrelated work impossible to verify. Their diagnostic value does not justify
			// halting the process: dedupe by stack, write it to the log, carry on. Set
			// OPENDEVELOP_ASSERT_DIALOG=1 to get the old blocking behavior back for a session where
			// you specifically want to catch an assert interactively.
			string report = message + Environment.NewLine + detailMessage + Environment.NewLine + stackTrace;
			ICSharpCode.Core.LoggingService.Warn("Debug.Assert/Trace.Fail: " + report);

			if (!ShowBlockingDialog)
				return;
			if (!dialogIsOpen.Set())
				return;
			// We might be unable to display a dialog here, e.g. because
			// we're on the UI thread but dispatcher processing is disabled.
			// In any case, we don't want to pump messages while the dialog is displaying,
			// so we create a separate UI thread for the dialog:
			bool debug = false;
			var thread = new Thread(() => ShowAssertionDialog(report, ref debug));
			// ApartmentState.STA relies on COM, which throws PlatformNotSupportedException on any
			// non-Windows platform. The WPF MessageBox below has no actual STA requirement on this
			// host (LibreWPF/macOS) - only set it on Windows, where it's still meaningful for
			// classic WinForms/COM interop concerns.
			if (OperatingSystem.IsWindows())
				thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			thread.Join();
			if (debug)
				Debugger.Break();
		}

		void ShowAssertionDialog(string report, ref bool debug)
		{
			// CustomDialog (WinForms multi-button dialog) is out of MVP scope - fall back to a plain WPF
			// message box with Yes(=Debug)/No(=Ignore) semantics ("ignore all" is now the default
			// behavior of Fail itself, which dedupes by stack before ever getting here).
			try {
				// An integration-test run has nobody to dismiss this, and a failed assertion would
				// otherwise hang the whole run behind a modal nobody can see. Log and ignore
				// (debug=false) so the assertion still shows up in the run's captured output.
				// Written straight to stderr (which the test fixture captures) rather than through
				// LoggingService: this type IS the trace listener, so logging from here can feed
				// back into itself.
				if (TestMode.IsActive) {
					Console.Error.WriteLine("OD_TEST_MODE: suppressed assertion dialog, auto-answered Ignore:" + Environment.NewLine + report);
					return;
				}

				var result = System.Windows.MessageBox.Show(
					report.TakeStartEllipsis(750) + Environment.NewLine + Environment.NewLine +
					"Yes = Debug, No = Ignore",
					"Assertion Failed", MessageBoxButton.YesNo);
				if (result == MessageBoxResult.Yes)
					debug = true;
			} finally {
				dialogIsOpen.Reset();
			}
		}
	}
}
