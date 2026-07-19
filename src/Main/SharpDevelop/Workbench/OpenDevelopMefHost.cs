using System;
using System.IO;
using System.Reflection;

using ICSharpCode.Core;
using Microsoft.VisualStudio.Composition;

namespace ICSharpCode.SharpDevelop.Workbench;

internal static class OpenDevelopMefHost
{
    // Cache is keyed on the executing assembly's write time, not its content hash - MEF part
    // discovery only depends on the assembly's attributes, and reading the write time is orders
    // of magnitude cheaper than hashing the whole DLL on every startup.
    const string CacheFileName = "mef-composition.cache";

    private static readonly Lazy<ExportProvider> LazyExportProvider = new(BuildExportProvider);

    public static ExportProvider ExportProvider => LazyExportProvider.Value;

    private static ExportProvider BuildExportProvider()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var cachePath = GetCachePath();
        var cached = TryLoadFromCache(cachePath, assembly);
        if (cached != null)
            return cached.CreateExportProvider();

        var discovery = new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true);
        var discoveredParts = discovery.CreatePartsAsync(assembly).GetAwaiter().GetResult();
        var catalog = ComposableCatalog.Create(Resolver.DefaultInstance).AddParts(discoveredParts.Parts);
        var configuration = CompositionConfiguration.Create(catalog);

        TrySaveToCache(cachePath, configuration, assembly);

        return configuration.CreateExportProviderFactory().CreateExportProvider();
    }

    static string GetCachePath()
    {
        var configDirectory = ICSharpCode.Core.PropertyService.ConfigDirectory?.ToString();
        if (string.IsNullOrEmpty(configDirectory))
            return null;
        return Path.Combine(configDirectory, CacheFileName);
    }

    static IExportProviderFactory TryLoadFromCache(string cachePath, Assembly assembly)
    {
        if (cachePath == null || !File.Exists(cachePath))
            return null;
        try {
            if (File.GetLastWriteTimeUtc(cachePath) < File.GetLastWriteTimeUtc(assembly.Location))
                return null;

            using var stream = File.OpenRead(cachePath);
            return new CachedComposition().LoadExportProviderFactoryAsync(stream, Resolver.DefaultInstance)
                .GetAwaiter().GetResult();
        } catch (Exception ex) {
            // Corrupt or version-mismatched cache (e.g. after a Microsoft.VisualStudio.Composition
            // upgrade) - fall back to a fresh scan rather than failing startup.
            LoggingService.Warn("OpenDevelopMefHost: failed to load MEF composition cache, rebuilding. " + ex.Message);
            return null;
        }
    }

    static void TrySaveToCache(string cachePath, CompositionConfiguration configuration, Assembly assembly)
    {
        if (cachePath == null)
            return;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
            using (var stream = File.Create(cachePath)) {
                new CachedComposition().SaveAsync(configuration, stream).GetAwaiter().GetResult();
            }
            File.SetLastWriteTimeUtc(cachePath, File.GetLastWriteTimeUtc(assembly.Location));
        } catch (Exception ex) {
            LoggingService.Warn("OpenDevelopMefHost: failed to write MEF composition cache. " + ex.Message);
        }
    }
}
