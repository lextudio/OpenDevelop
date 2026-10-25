using System;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Debugging
{
	/// <summary>
	/// Shows the decompiled code of a stack frame that has no source file. Implemented by the
	/// decompiler AddIn (registered at <see cref="AddInTreePath"/>) so the debugger AddIn does not
	/// have to reference it. doc/technotes/debugging.md, "Opening an external (decompiled) stack frame".
	/// </summary>
	public interface IDecompiledSourceNavigator
	{
		const string AddInTreePath = "/SharpDevelop/Services/DecompiledSourceNavigator";

		/// <summary>
		/// Opens the decompiled declaring type of the method and moves the caret to the statement
		/// containing <paramref name="ilOffset"/>.
		/// </summary>
		/// <param name="assemblyPath">Path of the assembly containing the frame's method.</param>
		/// <param name="typeFullName">Metadata type name; nested types joined with '+'.</param>
		/// <param name="methodToken">The method's metadata token.</param>
		/// <param name="ilOffset">IL offset of the frame, or -1 when unknown (caret goes to the method start).</param>
		/// <returns>The path of the document that was opened, or null when the frame could not be shown.</returns>
		Task<string> NavigateAsync(string assemblyPath, string typeFullName, int methodToken, int ilOffset);
	}
}
