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
using System.Text;

using log4net;

namespace ICSharpCode.SharpDevelop.Logging
{
	/// <summary>
	/// WPF replacement for the classic SharpDevelop WinForms crash dialog (removed during the
	/// .NET 11/WinForms-removal port). Unlike a plain <see cref="System.Windows.MessageBox"/>,
	/// this shows the exception in a read-only, selectable/copyable <see cref="System.Windows.Controls.TextBox"/>
	/// - a bare MessageBox renders its text as static content with no way to select or copy it.
	/// </summary>
	public static class ExceptionBox
	{
		/// <summary>
		/// Shows the exception dialog. Returns true if the user chose "Continue" (only offered
		/// when <paramref name="allowContinue"/> is set); callers that cannot sensibly continue
		/// (e.g. a startup failure before the workbench exists) should pass false and ignore the
		/// result.
		/// </summary>
		public static bool ShowErrorBox(Exception exception, string caption, bool allowContinue = false)
		{
			string details = BuildDetailsText(exception);
			try {
				var window = new ExceptionBoxWindow(caption, details, allowContinue);
				try {
					window.Owner = System.Windows.Application.Current?.MainWindow;
				} catch { }
				window.ShowDialog();
				return window.ContinueRunning;
			} catch (Exception dialogFailure) {
				// Building this window needs the WPF resource system (InitializeComponent ->
				// Application.LoadComponent), which throws "The Application object is being shut
				// down." once shutdown has started - precisely when a startup/shutdown failure is
				// being reported. Letting that escape replaces the real error with a confusing
				// secondary one and loses the original entirely, so fall back to a plain message
				// box and make sure the text still reaches the log either way.
				ICSharpCode.Core.LoggingService.Error("Could not show the exception dialog; original error follows.", dialogFailure);
				ICSharpCode.Core.LoggingService.Error(details);
				try {
					System.Windows.MessageBox.Show(details, caption);
				} catch {
					// Even the fallback can fail this late in shutdown; the log above is then the
					// only record, which is why it is written before this point.
				}
				return false;
			}
		}

		/// <summary>
		/// Shows a plain error message in the same read-only, selectable/copyable text box the
		/// exception dialog uses. MessageBox.Show renders its text as a non-selectable label (a
		/// native alert on macOS), so an error reported through it cannot be copied - which is
		/// useless for anything the user then wants to search for or paste into a bug report.
		/// </summary>
		public static void ShowCopyableError(string caption, string message, System.Windows.Window owner)
		{
			// Only the message goes in the box: unlike a crash, a reported error has no stack and
			// no need for the version/OS/log-tail preamble BuildDetailsText adds - that would bury
			// a one-line message and make a routine error look like a crash report.
			var window = new ExceptionBoxWindow(caption, message ?? string.Empty, allowContinue: false,
			                                    title: "Error", dismissOnly: true);
			// An owner is required, not optional: without one this dialog would be the app's only
			// window while the workbench is still starting, and WPF's default OnLastWindowClose
			// shutdown mode would then terminate the whole app the moment it is dismissed.
			// WpfMessageService only takes this path once it has a real owner window.
			try {
				window.Owner = owner;
			} catch { }
			window.ShowDialog();
		}

		static string BuildDetailsText(Exception exception)
		{
			var sb = new StringBuilder();
			sb.Append("SharpDevelop ").AppendLine(RevisionClass.FullVersion);
			sb.Append("Runtime: ").AppendLine(Environment.Version.ToString());
			sb.Append("OS: ").AppendLine(Environment.OSVersion.ToString());
			sb.AppendLine();
			sb.AppendLine(exception.ToString());
			try {
				sb.AppendLine();
				sb.AppendLine("Recent log messages:");
				LogMessageRecorder.AppendRecentLogMessages(sb, LogManager.GetLogger(typeof(ExceptionBox)));
			} catch {
				// The "Recorder" appender might not be configured (e.g. a stripped-down
				// log4net config) - the exception text above is still complete without it.
			}
			return sb.ToString();
		}
	}
}
