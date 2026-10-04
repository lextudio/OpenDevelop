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
using System.IO;
using System.Reflection;

namespace ICSharpCode.Core
{
	// Based on http://ayende.com/Blog/archive/2006/05/22/SolvingTheAssemblyLoadContextProblem.aspx
	// This class ensures that assemblies loaded into the LoadFrom context are also available
	// in the Load context.
	static class AssemblyLocator
	{
		static Dictionary<string, Assembly> assemblies = new Dictionary<string, Assembly>();
		static bool initialized;
		
		public static void Init()
		{
			lock (assemblies) {
				if (initialized)
					return;
				// Add-ins are loaded with Assembly.LoadFrom. Its dependency bind can fall back to
				// AppDomain.AssemblyResolve, but the host assemblies (ICSharpCode.SharpDevelop,
				// ICSharpCode.Core, ...) were normally loaded *before* this locator subscribed to
				// AssemblyLoad. Seed the cache so a subsequently loaded add-in can bind to those
				// already-present host assemblies instead of reporting its classes as missing.
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
					assemblies[assembly.FullName] = assembly;
				initialized = true;
				AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;
				AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
			}
		}
		
		static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
		{
			lock (assemblies) {
				Assembly assembly = null;
				if (assemblies.TryGetValue(args.Name, out assembly))
					return assembly;

				// AddIns intentionally do not copy OpenDevelop-provided assemblies beside every
				// addin.  A host dependency may not have been loaded yet when Init() seeds the
				// cache, though, so the cache alone cannot satisfy the first such bind.  Resolve
				// the exact assembly name from the application's payload directory, which keeps
				// one authoritative copy in OpenDevelop rather than duplicating it per AddIn.
				var simpleName = new AssemblyName(args.Name).Name;
				if (string.IsNullOrEmpty(simpleName))
					return null;
				var candidate = Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
				if (!File.Exists(candidate))
					return null;
				assembly = Assembly.LoadFrom(candidate);
				assemblies[assembly.FullName] = assembly;
				return assembly;
			}
		}
		
		static void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
		{
			Assembly assembly = args.LoadedAssembly;
			lock (assemblies) {
				assemblies[assembly.FullName] = assembly;
			}
		}
	}
}
