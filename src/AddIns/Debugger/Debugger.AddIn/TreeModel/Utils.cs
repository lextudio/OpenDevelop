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

using ICSharpCode.Core;

namespace Debugger.AddIn.TreeModel
{
	// The old Process.EnqueueWork/EnqueueForEach helpers (which polled ICorDebug's
	// Process.DebuggeeState to avoid acting on stale evaluations after stepping) have no DAP
	// equivalent and are no longer needed: DAP evaluation is a plain request/response over the
	// adapter connection, not tied to a mutable in-process debuggee snapshot.

	public class AbortedBecauseDebuggeeResumedException : System.Exception
	{
		public AbortedBecauseDebuggeeResumedException() : base()
		{
		}
	}

	public class PrintTimes : PrintTime
	{
		public PrintTimes(string text) : base(text + " - end")
		{
			LoggingService.InfoFormatted("{0} - start", text);
		}
	}

	public class PrintTime : System.IDisposable
	{
		string text;
		System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();

		public PrintTime(string text)
		{
			this.text = text;
			stopwatch.Start();
		}

		public void Dispose()
		{
			stopwatch.Stop();
			LoggingService.InfoFormatted("{0} ({1} ms)", text, stopwatch.ElapsedMilliseconds);
		}
	}

	public static class ExtensionMethods
	{
		public static Debugger.AddIn.Pads.Controls.SharpTreeNodeAdapter ToSharpTreeNode(this TreeNode node)
		{
			return new Debugger.AddIn.Pads.Controls.SharpTreeNodeAdapter(node);
		}
	}
}
