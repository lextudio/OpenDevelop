using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Debugging
{
	/// <summary>
	/// Produces the symbols (a portable PDB and the decompiled source files it points to) for an assembly that has none, so the
	/// debugger can stop, step and set breakpoints in decompiled code. Implemented by the decompiler AddIn (registered at
	/// <see cref="AddInTreePath"/>) so the debugger AddIn does not have to reference it. <b>With no implementation registered
	/// (the decompiler AddIn is disabled) nothing is decompiled while debugging.</b>
	/// doc/technotes/decompiled-debugging.md.
	/// </summary>
	public interface IDecompiledSymbolProvider
	{
		const string AddInTreePath = "/SharpDevelop/Services/DecompiledSymbolProvider";

		/// <summary>
		/// Generates (or returns the cached) symbols for the module.
		/// </summary>
		/// <param name="assemblyPath">Path of the module as the debuggee loaded it.</param>
		/// <param name="mvid">The module's MVID; the cache key, because the same path can be a different build.</param>
		/// <param name="progress">Receives short status lines for the Debug output.</param>
		/// <param name="cancellationToken">Ends this caller's wait only; a generation that is under way is shared and finishes, so the next request finds it.</param>
		/// <returns>The path of the portable PDB, whose documents are the decompiled files on disk; null when none could be produced.</returns>
		Task<string> GetSymbolsAsync(string assemblyPath, Guid mvid, IProgress<string> progress, CancellationToken cancellationToken);
	}
}
