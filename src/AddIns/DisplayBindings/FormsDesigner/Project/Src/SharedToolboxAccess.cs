// Bridges the WinForms designer to the merged shared Toolbox (Base's SharedToolbox, whose
// "winforms" scope WpfDesign.AddIn's WpfToolbox facade seeds). The two AddIns have no
// compile-time reference to each other, so the facade is touched through reflection once - a
// pure WinForms session never loads WpfDesign.AddIn otherwise, and the Tools pad would mount
// nothing (ToolsContent must return a real control at view-activation time, the way
// WinUIXamlDesignerViewContent.ToolsContent constructs its facade eagerly).

using System;
using System.IO;
using System.Runtime.Loader;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.FormsDesigner
{
	internal static class SharedToolboxAccess
	{
		static ISharedToolboxHost host;

		/// <summary>Ensures the shared toolbox facade exists (constructing WpfDesign.AddIn's
		/// WpfToolbox via reflection when a pure WinForms session has not loaded it yet) and
		/// returns it as an <see cref="ISharedToolboxHost"/>.</summary>
		public static ISharedToolboxHost Host {
			get {
				var current = SD.Services.GetService(typeof(ISharedToolboxHost)) as ISharedToolboxHost;
				if (current != null)
					return host = current;
				if (host != null)
					return host;

				var toolboxType = Type.GetType("ICSharpCode.WpfDesign.AddIn.WpfToolbox, ICSharpCode.WpfDesign.AddIn");
				if (toolboxType == null) {
					// The WpfDesign addin assembly is only loaded once some .xaml is opened;
					// fall back to loading it from disk (its AddIns dir sits next to ours),
					// resolving the sibling designer assemblies on demand.
					var wpfDir = Path.Combine(
						Path.GetDirectoryName(typeof(FormsDesignerViewContent).Assembly.Location), "..", "WpfDesign");
					var loadContext = AssemblyLoadContext.Default;
					loadContext.Resolving += (_, name) => {
						var candidate = Path.Combine(wpfDir, name.Name + ".dll");
						return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
					};
					var path = Path.Combine(wpfDir, "ICSharpCode.WpfDesign.AddIn.dll");
					if (File.Exists(path))
						toolboxType = loadContext.LoadFromAssemblyPath(path)
							.GetType("ICSharpCode.WpfDesign.AddIn.WpfToolbox");
				}
				host = toolboxType?.GetProperty("Instance",
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
					?.GetValue(null) as ISharedToolboxHost;
				return host;
			}
		}

		/// <summary>Seeds the shared toolbox (constructing the facade if needed) and returns its
		/// control - the same object the Tools pad mounts.</summary>
		public static object ToolboxControl => Host?.ToolboxControl;
	}
}
