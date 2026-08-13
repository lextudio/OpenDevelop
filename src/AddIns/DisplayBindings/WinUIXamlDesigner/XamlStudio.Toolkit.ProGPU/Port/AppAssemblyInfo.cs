using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using XamlStudio.Toolkit.Helpers;

namespace XamlStudio.Toolkit.Services;

public sealed class AppAssemblyInfo
{
    public static AppAssemblyInfo Instance => Singleton<AppAssemblyInfo>.Instance;
    public bool IsLoaded { get; private set; }
    public IReadOnlyList<Assembly> LoadedAssemblies { get; private set; } = Array.Empty<Assembly>();
    public IReadOnlyList<Type> KnownTypes { get; private set; } = Array.Empty<Type>();
    public IReadOnlyDictionary<string, ReadOnlyCollection<Type>> TypesByNamespace { get; private set; }
        = new Dictionary<string, ReadOnlyCollection<Type>>();

    public Task InitializeAsync(Assembly[] extraAssemblies = null)
    {
        if (IsLoaded) return Task.CompletedTask;
        LoadedAssemblies = (extraAssemblies ?? Array.Empty<Assembly>())
            .Append(typeof(FrameworkElement).Assembly).Distinct().ToArray();
        KnownTypes = LoadedAssemblies.SelectMany(SafeTypes).ToArray();
        TypesByNamespace = KnownTypes.Where(type => type.Namespace != null)
            .GroupBy(type => type.Namespace)
            .ToDictionary(group => group.Key, group => new ReadOnlyCollection<Type>(group.ToList()));
        IsLoaded = true;
        return Task.CompletedTask;
    }

    static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type != null); }
    }
}
