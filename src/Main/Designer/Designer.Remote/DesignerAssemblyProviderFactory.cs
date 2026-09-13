using System;
using System.Collections.Generic;
using System.Reflection;

namespace ICSharpCode.SharpDevelop.Designer.Remote;

/// <summary>
/// Locates a framework-supplied service implementation in a designer-loaded assembly.
///
/// XAML runtimes generate metadata-provider classes next to the controls they describe, but
/// the generated class name is deliberately an implementation detail.  Keeping that discovery
/// here lets individual out-of-process hosts adapt their own service contract without coupling
/// the common designer layer to WinUI, WPF, Avalonia, or Uno APIs.
/// </summary>
public static class DesignerAssemblyProviderFactory
{
	/// <summary>
	/// Creates the first non-abstract, public parameterless implementation of
	/// <typeparamref name="TService"/> in <paramref name="assembly"/>.  An assembly with missing
	/// optional dependencies remains usable: loadable types are still considered and a failing
	/// constructor is skipped.
	/// </summary>
	public static TService? CreateFirst<TService>(Assembly assembly) where TService : class
	{
		ArgumentNullException.ThrowIfNull(assembly);
		foreach (var candidate in GetLoadableTypes(assembly)) {
			if (candidate.IsAbstract
				|| !typeof(TService).IsAssignableFrom(candidate)
				|| candidate.GetConstructor(Type.EmptyTypes) is null)
				continue;
			try {
				if (Activator.CreateInstance(candidate) is TService service)
					return service;
			} catch {
				// A candidate may itself need an optional runtime dependency. Keep looking so a
				// third-party control library cannot disable the host's reflection fallback.
			}
		}
		return null;
	}

	static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
	{
		try {
			return assembly.GetTypes();
		} catch (ReflectionTypeLoadException e) {
			var loadable = new List<Type>();
			foreach (var type in e.Types) {
				if (type is not null) loadable.Add(type);
			}
			return loadable;
		} catch {
			return Array.Empty<Type>();
		}
	}
}
