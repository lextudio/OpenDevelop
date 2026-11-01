using System.Reflection;
using System.Runtime.Loader;

namespace ICSharpCode.GtkDesigner.Host;

/// <summary>
/// Loads the project assemblies a <c>.gtkdesigner.json</c> declares into this design host - the
/// disposable child process, never the IDE - and registers their Gtk.Widget subclasses so GtkBuilder
/// can create them (doc/technotes/gtk-designer.md, "Custom widget execution"). Gir.Core registers a
/// GObject subclass's GType from its static GetGType(), which is what runs here. Each path is loaded
/// once per host: a GType cannot be unregistered, so a rebuilt assembly needs a fresh host (the
/// designer says so). Every failure becomes a diagnostic and leaves the labelled placeholder.
/// </summary>
static class GtkCustomWidgetLoader
{
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
	static readonly object Gate = new();
	static readonly Dictionary<string, Loaded> loaded = new(StringComparer.Ordinal);
	static readonly HashSet<string> registered = new(StringComparer.Ordinal);

	sealed record Loaded(DateTime WriteTimeUtc, string? Failure, IReadOnlyList<string> Types);

	public static bool IsRegistered(string gtypeName) { lock (Gate) return registered.Contains(gtypeName); }

	/// <summary>Makes sure each assembly is loaded (once) and reports problems; returns nothing, the
	/// registered names are asked through <see cref="IsRegistered"/>.</summary>
	public static void Ensure(IEnumerable<string> assemblyPaths, IList<string> diagnostics)
	{
		foreach (var path in assemblyPaths) {
			var written = File.GetLastWriteTimeUtc(path);
			Loaded? known; lock (Gate) loaded.TryGetValue(path, out known);
			if (known == null) {
				known = LoadWithTimeout(path, written);
				lock (Gate) { loaded[path] = known; foreach (var name in known.Types) registered.Add(name); }
			} else if (known.WriteTimeUtc != written) {
				diagnostics.Add("'" + path + "' was rebuilt after the design host loaded it; reopen the designer to preview the new widgets.");
			}
			if (known.Failure != null) diagnostics.Add(known.Failure);
		}
	}

	static Loaded LoadWithTimeout(string path, DateTime written)
	{
		var task = Task.Run(() => Load(path, written));
		return task.Wait(Timeout) ? task.Result : new Loaded(written, "Loading custom widgets from '" + path + "' did not finish in " + Timeout.TotalSeconds + " s; they are previewed as placeholders.", Array.Empty<string>());
	}

	static Loaded Load(string path, DateTime written)
	{
		try {
			var assembly = new Context(path).LoadFromAssemblyPath(path);
			Type[] types;
			try { types = assembly.GetTypes(); }
			catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
			var names = new List<string>();
			foreach (var type in types) {
				if (type.IsAbstract || !typeof(Gtk.Widget).IsAssignableFrom(type)) continue;
				if (type.GetMethod("GetGType", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly, Type.EmptyTypes) is not { } method) continue;
				try {
					var gtype = (GObject.Type)method.Invoke(null, null)!;
					names.Add(GObject.Functions.TypeName(gtype));
				} catch (Exception ex) {
					return new Loaded(written, "Registering custom widget '" + type.FullName + "' from '" + path + "' failed: " + (ex.InnerException ?? ex).Message, names);
				}
			}
			return new Loaded(written, names.Count == 0 ? "'" + path + "' declares no Gtk.Widget subclass the design host could register." : null, names);
		} catch (Exception ex) {
			return new Loaded(written, "Could not load custom widgets from '" + path + "': " + ex.Message, Array.Empty<string>());
		}
	}

	/// <summary>Resolves the project's own dependencies from its deps.json, but leaves everything the
	/// host already has (Gir.Core above all) to the default context, so a widget derives from the very
	/// Gtk.Widget this host renders with.</summary>
	sealed class Context : AssemblyLoadContext
	{
		readonly AssemblyDependencyResolver resolver;
		public Context(string path) : base("GtkCustomWidgets:" + Path.GetFileName(path)) => resolver = new AssemblyDependencyResolver(path);

		protected override Assembly? Load(AssemblyName name)
		{
			try { Default.LoadFromAssemblyName(name); return null; } catch (FileNotFoundException) { }
			var resolved = resolver.ResolveAssemblyToPath(name);
			return resolved == null ? null : LoadFromAssemblyPath(resolved);
		}
	}
}
