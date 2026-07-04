// Real "glue" over VS MEF for the one thing its public API doesn't support: constructing a part
// that needs a specific per-call runtime instance (e.g. one project's UnconfiguredProject) in its
// [ImportingConstructor], which plain AttributedPartDiscovery/ExportProvider can't parameterize
// since discovery works off static types, not per-call arguments (see docs/project-system.md,
// Slice 44's "deliberately not attempted" note).
//
// Unlike Dataflow/ManualComposition.cs (slice 43), which hardcodes field names and a fixed value
// list per call site, this drives everything from the real [ImportingConstructor]/[ImportMany]
// attribute metadata via reflection: given a target type, it resolves constructor parameters and
// ImportMany fields the same way a real MEF container would when *activating* a part — the parts
// of composition that are genuinely per-call-instance-scoped, layered on top of the real
// RealMefHost.ExportProvider for everything else. See docs/project-system.md (Slice 45).

using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.VisualStudio.Composition;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>
/// A per-call composition scope: real VS MEF exports (<see cref="ExportProvider"/>) plus a small
/// set of runtime instance overrides that aren't discoverable as static parts (project-specific
/// values like <c>UnconfiguredProject</c>).
/// </summary>
internal sealed class CompositionScope
{
    private sealed class FixedOrderMetadataView : IOrderPrecedenceMetadataView
    {
        public int OrderPrecedence => 0;
        public string[] AppliesTo => Array.Empty<string>();
    }

    private static readonly IOrderPrecedenceMetadataView Metadata = new FixedOrderMetadataView();

    private readonly ExportProvider _exportProvider;
    private readonly Dictionary<Type, object> _instanceOverrides = new();
    private readonly Dictionary<Type, IEnumerable<object>> _manyOverrides = new();

    public CompositionScope(ExportProvider exportProvider) => _exportProvider = exportProvider;

    /// <summary>Registers a specific runtime instance to satisfy imports of type <typeparamref name="T"/> within this scope.</summary>
    public CompositionScope WithInstance<T>(T instance) where T : notnull
    {
        _instanceOverrides[typeof(T)] = instance;
        return this;
    }

    /// <summary>
    /// Registers a fixed set of values to satisfy <c>[ImportMany]</c> fields of type
    /// <typeparamref name="T"/>, bypassing the real <see cref="ExportProvider"/> — needed when the
    /// real assembly also contains other <c>[Export(typeof(T))]</c> parts that need per-project
    /// runtime data plain discovery can't supply (e.g. other <c>IDependencySubscriber</c>/
    /// <c>IDependencySliceSubscriber</c> exports), which would otherwise fail composition when
    /// the real provider tries to construct them itself.
    /// </summary>
    public CompositionScope WithManyInstances<T>(IEnumerable<T> values) where T : notnull
    {
        _manyOverrides[typeof(T)] = values.Cast<object>();
        return this;
    }

    /// <summary>
    /// Constructs <typeparamref name="T"/> by resolving its <c>[ImportingConstructor]</c> parameters
    /// from this scope (instance overrides first, then the real <see cref="ExportProvider"/>), then
    /// populates any <c>[ImportMany]</c> fields the same way. Equivalent to what a real MEF container
    /// does when activating a part — just scoped to this one call instead of a static catalog.
    /// </summary>
    public T Activate<T>() where T : class
    {
        var type = typeof(T);
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(c => c.GetCustomAttribute<ImportingConstructorAttribute>() is not null)
            ?? type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();

        var args = ctor.GetParameters().Select(p => ResolveSingle(p.ParameterType, p.Name)).ToArray();
        var instance = (T)ctor.Invoke(args);

        PopulateImportManyFields(instance);

        return instance;
    }

    private void PopulateImportManyFields(object instance)
    {
        foreach (var field in instance.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.GetCustomAttribute<ImportManyAttribute>() is null)
            {
                continue;
            }

            if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(OrderPrecedenceImportCollection<>))
            {
                var contractType = field.FieldType.GetGenericArguments()[0];
                var collection = field.GetValue(instance)
                    ?? throw new InvalidOperationException($"'{field.Name}' on {instance.GetType()} was not initialized before composition.");

                var addMethod = collection.GetType().GetMethod("Add")
                    ?? throw new InvalidOperationException($"OrderPrecedenceImportCollection<{contractType}> has no Add method.");
                var lazyType = typeof(Lazy<,>).MakeGenericType(contractType, typeof(IOrderPrecedenceMetadataView));
                var funcType = typeof(Func<>).MakeGenericType(contractType);

                foreach (var value in ResolveMany(contractType))
                {
                    // Lazy<T, TMetadata>'s constructor requires an exactly-typed Func<T>, not
                    // Func<object> — build one via Expression rather than a cast, which the CLR
                    // constructor-binding rejects for value-returning delegate covariance reasons.
                    var valueFactory = Expression.Lambda(funcType, Expression.Constant(value, contractType)).Compile();
                    var lazy = Activator.CreateInstance(lazyType, valueFactory, Metadata)!;
                    addMethod.Invoke(collection, new[] { lazy });
                }
            }
        }
    }

    private object ResolveSingle(Type type, string? parameterName)
    {
        if (_instanceOverrides.TryGetValue(type, out var overridden))
        {
            return overridden;
        }

        try
        {
            // ExportProvider.GetExportedValue<T>() computes the default contract name for T
            // internally (matching [Export(typeof(T))]) — that logic is a VS MEF implementation
            // detail (ContractNameServices) that isn't public, so we invoke the real generic
            // method reflectively for a runtime Type rather than reimplementing contract-name
            // derivation ourselves.
            return GetExportedValueMethod.MakeGenericMethod(type).Invoke(_exportProvider, null)
                ?? throw new InvalidOperationException($"No export found for parameter '{parameterName}' of type {type}.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not resolve parameter '{parameterName}' of type {type}: not registered as a scope instance override and not found in the real VS MEF ExportProvider.", ex.InnerException ?? ex);
        }
    }

    private IEnumerable<object> ResolveMany(Type contractType)
    {
        if (_manyOverrides.TryGetValue(contractType, out var overridden))
        {
            return overridden;
        }

        var result = GetExportedValuesMethod.MakeGenericMethod(contractType).Invoke(_exportProvider, null)!;
        return ((IEnumerable)result).Cast<object>();
    }

    private static readonly MethodInfo GetExportedValueMethod = typeof(ExportProvider).GetMethods()
        .Single(m => m.Name == nameof(ExportProvider.GetExportedValue) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);

    private static readonly MethodInfo GetExportedValuesMethod = typeof(ExportProvider).GetMethods()
        .Single(m => m.Name == nameof(ExportProvider.GetExportedValues) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
}
