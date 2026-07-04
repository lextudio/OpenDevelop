// MVP mocks/extensions: small pieces of NRefactory surface (concrete implementation classes and a few
// convenience extension methods) not covered by the interface-only Abstractions library. Per the MVP
// task's mock policy - just enough shape to compile for a first-boot milestone, not real semantics.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICSharpCode.TypeSystem
{
	/// <summary>
	/// Mock of NRefactory's MinimalCorlib - a fake/offline "mscorlib" assembly reference used as a
	/// last-resort fallback so resolution code always has at least one reference. Resolving it always
	/// returns null in this MVP build; nothing exercises the resolved assembly.
	/// </summary>
	public sealed class MinimalCorlib : IAssemblyReference
	{
		public static readonly MinimalCorlib Instance = new MinimalCorlib();

		public IAssembly Resolve(ITypeResolveContext context) => null;

		/// <summary>
		/// Creates an empty fallback compilation with only this fake corlib as a reference - used when no
		/// real project/compilation is available. No real type resolution happens against it.
		/// </summary>
		public ICompilation CreateCompilation()
		{
			return new SimpleCompilation(null, new IAssemblyReference[] { this });
		}
	}

	/// <summary>
	/// Mock of NRefactory's FreezableHelper.Freeze() - marked an object graph as read-only/immutable for
	/// thread safety. No-op in this MVP build (parsed files are never actually frozen against mutation).
	/// </summary>
	public static class FreezableHelper
	{
		public static void Freeze(object obj)
		{
		}
	}

	/// <summary>
	/// Small, generic (non-NRefactory-specific) double-checked-initialization helper. Originally
	/// ICSharpCode.NRefactory.Utils.LazyInit.
	/// </summary>
	public static class LazyInit
	{
		public static T GetOrSet<T>(ref T target, T value) where T : class
		{
			T oldValue = System.Threading.Interlocked.CompareExchange(ref target, value, null);
			return oldValue ?? value;
		}
	}

	/// <summary>
	/// Disposable that invokes a callback on Dispose(). (Originally ICSharpCode.NRefactory.Utils.CallbackOnDispose -
	/// a tiny, generic, non-NRefactory-specific helper; copied in directly rather than mocked as a stub since
	/// its real behavior is one line.)
	/// </summary>
	public sealed class CallbackOnDispose : IDisposable
	{
		Action callback;

		public CallbackOnDispose(Action callback)
		{
			this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
		}

		public void Dispose()
		{
			Interlocked_Exchange(ref callback, null)?.Invoke();
		}

		static Action Interlocked_Exchange(ref Action location, Action value)
		{
			return System.Threading.Interlocked.Exchange(ref location, value);
		}
	}

	/// <summary>
	/// Minimal mock of NRefactory's SimpleCompilation. Does not perform real type resolution; MainAssembly
	/// is resolved eagerly against itself (best-effort) and most other members return empty/default values.
	/// </summary>
	public class SimpleCompilation : ICompilation
	{
		public IAssembly MainAssembly { get; }
		public IList<IAssembly> Assemblies { get; } = new List<IAssembly>();
		public IList<IAssembly> ReferencedAssemblies { get; } = new List<IAssembly>();
		public ITypeResolveContext TypeResolveContext { get; }
		public INamespace RootNamespace => null;
		public StringComparer NameComparer => StringComparer.Ordinal;
		public ISolutionSnapshot SolutionSnapshot => null;
		public CacheManager CacheManager { get; } = new CacheManager();

		public SimpleCompilation(IUnresolvedAssembly mainAssembly, IEnumerable<IAssemblyReference> assemblyReferences)
		{
			this.TypeResolveContext = new SimpleTypeResolveContext(this);
			try {
				this.MainAssembly = mainAssembly?.Resolve(this.TypeResolveContext);
			} catch (NotImplementedException) {
				this.MainAssembly = null;
			}
			if (this.MainAssembly != null)
				Assemblies.Add(this.MainAssembly);
		}

		public INamespace GetNamespaceForExternAlias(string alias) => RootNamespace;

		public IType FindType(KnownTypeCode typeCode) => null;
	}

	/// <summary>
	/// Minimal mock of NRefactory's SimpleTypeResolveContext.
	/// </summary>
	public class SimpleTypeResolveContext : ITypeResolveContext
	{
		public ICompilation Compilation { get; }
		public IAssembly CurrentAssembly { get; }
		public ITypeDefinition CurrentTypeDefinition { get; }
		public IMember CurrentMember { get; }

		public SimpleTypeResolveContext(ICompilation compilation)
		{
			this.Compilation = compilation;
		}

		public SimpleTypeResolveContext(ITypeDefinition typeDefinition)
		{
			this.Compilation = typeDefinition?.Compilation;
			this.CurrentTypeDefinition = typeDefinition;
		}

		SimpleTypeResolveContext(ICompilation compilation, IAssembly assembly, ITypeDefinition typeDefinition, IMember member)
		{
			this.Compilation = compilation;
			this.CurrentAssembly = assembly;
			this.CurrentTypeDefinition = typeDefinition;
			this.CurrentMember = member;
		}

		public ITypeResolveContext WithCurrentTypeDefinition(ITypeDefinition typeDefinition)
			=> new SimpleTypeResolveContext(Compilation, CurrentAssembly, typeDefinition, CurrentMember);

		public ITypeResolveContext WithCurrentMember(IMember member)
			=> new SimpleTypeResolveContext(Compilation, CurrentAssembly, CurrentTypeDefinition, member);
	}

	/// <summary>
	/// Minimal mock of NRefactory's DefaultUnresolvedMethod. Deliberately does NOT implement IUnresolvedMethod
	/// (that interface has ~25 members - real implementation is out of scope for a mock). The only call sites
	/// (CompletionImage.cs, GotoDialog.cs) do `method as DefaultUnresolvedMethod` on an IUnresolvedMethod-typed
	/// value and check `.IsExtensionMethod` - an `as`-cast to an unrelated class compiles fine and simply always
	/// evaluates to null at runtime here, which is an acceptable no-op for a first-boot milestone (no method
	/// will ever actually be reported as an extension method by this mock).
	/// </summary>
	public class DefaultUnresolvedMethod
	{
		public bool IsExtensionMethod { get; set; }
	}

	/// <summary>
	/// Mock of NRefactory's ReflectionHelper.ParseReflectionName extension point used by TypeResolutionService.
	/// </summary>
	public static class ReflectionHelperExtensions
	{
		public static ITypeReference ParseReflectionName(this string reflectionTypeName)
		{
			return null;
		}
	}

	/// <summary>
	/// Mock of NRefactory's binary FastSerializer used to cache parsed type information to disk. No-op:
	/// the MVP build always re-parses instead of reading/writing a cache.
	/// </summary>
	public class FastSerializer
	{
		public void Serialize(BinaryWriter writer, object instance)
		{
			// no-op: parse-info disk caching is not implemented in this MVP build.
		}

		public object Deserialize(BinaryReader reader)
		{
			return null;
		}
	}

	public class BinaryWriterWith7BitEncodedInts : BinaryWriter
	{
		public BinaryWriterWith7BitEncodedInts(Stream stream) : base(stream) { }

		public new void Write7BitEncodedInt(int value) => base.Write7BitEncodedInt(value);
	}

	public class BinaryReaderWith7BitEncodedInts : BinaryReader
	{
		public BinaryReaderWith7BitEncodedInts(Stream stream) : base(stream) { }

		public new int Read7BitEncodedInt() => base.Read7BitEncodedInt();
	}

	public static class ProjectContentExtensions
	{
		/// <summary>
		/// Enumerates all type definitions (including nested) across all files in the project content.
		/// </summary>
		public static IEnumerable<IUnresolvedTypeDefinition> GetAllTypeDefinitions(this IProjectContent projectContent)
		{
			if (projectContent == null)
				yield break;
			foreach (var file in projectContent.Files) {
				foreach (var td in AllNested(file.TopLevelTypeDefinitions)) {
					yield return td;
				}
			}
		}

		public static IEnumerable<ITypeDefinition> GetAllTypeDefinitions(this IAssembly assembly)
		{
			yield break;
		}

		static IEnumerable<IUnresolvedTypeDefinition> AllNested(IEnumerable<IUnresolvedTypeDefinition> types)
		{
			foreach (var t in types) {
				yield return t;
				foreach (var n in AllNested(t.NestedTypes))
					yield return n;
			}
		}
	}
}
