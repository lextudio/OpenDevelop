#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SharpDevelop.Services;

/// <summary>
/// An addin-owned convention for files that belong together in Solution Explorer. Providers decide
/// both which otherwise-unknown file extensions belong in a project tree and which sibling file is
/// their display parent. They never alter MSBuild metadata.
/// </summary>
public interface IProjectFileNestingProvider
{
	/// <summary>Higher-priority providers are consulted first when more than one addin claims a
	/// display item. Keep the default value for a narrowly scoped convention; use a higher value
	/// only when an addin intentionally supplies the authoritative convention for the same files.</summary>
	int Priority => 0;

    /// <summary>Whether this provider applies to the project file.</summary>
    bool AppliesToProject(string projectFilePath);

    /// <summary>Whether a physical project item with this path should be shown by this provider.</summary>
    bool IsSupportedProjectItem(string projectFilePath, string physicalPath);

    /// <summary>Returns the display-relative path of a parent, or <see langword="null"/> when the
    /// item remains top-level. <paramref name="projectDisplayPaths"/> contains every visible item
    /// path in the project.</summary>
    string? GetParentDisplayPath(string projectFilePath, string displayPath, IReadOnlySet<string> projectDisplayPaths);
}

/// <summary>
/// Addin registration point for <see cref="IProjectFileNestingProvider"/>. It is deliberately a
/// registry rather than MEF: addin assemblies are loaded through the legacy AddIn tree and are not
/// scanned by the shell's MEF composition.
/// </summary>
public static class ProjectFileNestingProviderRegistry
{
    static readonly List<IProjectFileNestingProvider> providers = new();

    /// <summary>Raised after a provider is added or removed so an already-realized Projects pad can refresh.</summary>
    public static event EventHandler? Changed;

    public static IDisposable Register(IProjectFileNestingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var added = false;
        lock (providers)
        {
            if (!providers.Contains(provider)) {
                providers.Add(provider);
                added = true;
            }
        }
        if (added)
            Changed?.Invoke(null, EventArgs.Empty);
        return added ? new Registration(provider) : EmptyRegistration.Instance;
    }

    public static IReadOnlyList<IProjectFileNestingProvider> GetProviders()
    {
        lock (providers)
        {
			// List order is the stable tie breaker, so providers at the same priority retain their
			// AddIn loading order instead of depending on an unspecified sort implementation.
			return providers.Select((provider, index) => (provider, index))
				.OrderByDescending(item => item.provider.Priority)
				.ThenBy(item => item.index)
				.Select(item => item.provider)
				.ToArray();
        }
    }

    sealed class Registration : IDisposable
    {
        IProjectFileNestingProvider? provider;

        public Registration(IProjectFileNestingProvider provider) => this.provider = provider;

        public void Dispose()
        {
            var registeredProvider = System.Threading.Interlocked.Exchange(ref provider, null);
            if (registeredProvider is null)
                return;
            lock (providers)
            {
                providers.Remove(registeredProvider);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    sealed class EmptyRegistration : IDisposable
    {
        public static readonly EmptyRegistration Instance = new();
        public void Dispose() { }
    }
}
