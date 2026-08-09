// Copyright (c) 2026 The OpenDevelop Team
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

namespace ICSharpCode.SharpDevelop.ViewModels
{
	/// <summary>
	/// Lets an AddIn register a <see cref="ToolPaneModel"/> for a legacy AddInTree <c>&lt;Pad&gt;</c>
	/// without that AddIn being a MEF part of the App assembly.
	/// <see cref="AvalonDockLayout.GetMefToolPaneContentId"/> consults this registry after its
	/// MEF-backed <see cref="DockWorkspace.ToolPanes"/> lookup misses, and registers the resolved
	/// pane with the workspace so it joins the persisted layout like any built-in ToolPaneModel
	/// (doc/technotes/ilspy.md "Docking and layout replacement" item 4 consolidation, 2026-08-09).
	/// The factory is invoked lazily on first resolution - registration itself (e.g. from an
	/// Autostart command, which runs before the workbench is constructed) must not create the pane
	/// too early, since the pane's constructor may reach for services that only exist once the
	/// workbench is up.
	/// </summary>
	[CLSCompliant(true)]
	public static class PadToolPaneProvider
	{
		static readonly Dictionary<string, Func<ToolPaneModel>> factories = new Dictionary<string, Func<ToolPaneModel>>(StringComparer.Ordinal);
		static readonly Dictionary<string, ToolPaneModel> instances = new Dictionary<string, ToolPaneModel>(StringComparer.Ordinal);
		static readonly object syncRoot = new object();

		public static void Register(string legacyPadClass, Func<ToolPaneModel> factory)
		{
			if (legacyPadClass == null)
				throw new ArgumentNullException(nameof(legacyPadClass));
			if (factory == null)
				throw new ArgumentNullException(nameof(factory));
			lock (syncRoot) {
				factories[legacyPadClass] = factory;
			}
		}

		public static ToolPaneModel Resolve(string legacyPadClass)
		{
			if (legacyPadClass == null)
				return null;
			lock (syncRoot) {
				ToolPaneModel pane;
				if (instances.TryGetValue(legacyPadClass, out pane))
					return pane;
				Func<ToolPaneModel> factory;
				if (factories.TryGetValue(legacyPadClass, out factory)) {
					pane = factory();
					instances[legacyPadClass] = pane;
				}
				return pane;
			}
		}
	}
}
