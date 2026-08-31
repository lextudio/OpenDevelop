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
			var window = new ExceptionBoxWindow(caption, details, allowContinue);
			try {
				window.Owner = System.Windows.Application.Current?.MainWindow;
			} catch { }
			window.ShowDialog();
			return window.ContinueRunning;
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
