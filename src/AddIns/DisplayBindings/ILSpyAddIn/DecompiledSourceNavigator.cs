using System;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.TypeSystem;

namespace ICSharpCode.ILSpyAddIn
{
	/// <summary>
	/// Opens the decompiled declaring type of a debugger frame and puts the caret on the statement
	/// the frame is executing. The adapter's own decompiled-PDB line numbers are not used (it
	/// decompiles with separate settings); only the method token and IL offset are, looked up in
	/// the <see cref="DecompiledMethodDebugInfo"/> sequence points kept by the opened document.
	/// </summary>
	public sealed class DecompiledSourceNavigator : IDecompiledSourceNavigator
	{
		public async Task<string> NavigateAsync(string assemblyPath, string typeFullName, int methodToken, int ilOffset)
		{
			if (string.IsNullOrEmpty(assemblyPath) || string.IsNullOrEmpty(typeFullName) || !System.IO.File.Exists(assemblyPath))
				return null;

			int nested = typeFullName.IndexOf('+');
			string topLevel = nested < 0 ? typeFullName : typeFullName.Substring(0, nested);
			var reference = new DecompiledTypeReference(FileName.Create(assemblyPath), new TopLevelTypeName(topLevel));

			await NavigateToDecompiledEntityService.NavigateTo(FileName.Create(assemblyPath), topLevel, null).ConfigureAwait(true);

			var view = SD.Workbench.ViewContentCollection.OfType<DecompiledViewContent>()
				.FirstOrDefault(v => v.DecompiledTypeName.Equals(reference));
			if (view == null)
				return null;

			var symbols = view.DebugSymbols?.Values.FirstOrDefault(s => s.MethodDefToken == (uint)methodToken);
			if (symbols != null && symbols.SequencePoints.Count > 0) {
				var point = ilOffset >= 0
					? symbols.SequencePoints.FirstOrDefault(p => !p.IsHidden && p.Offset <= ilOffset && ilOffset < p.EndOffset)
					: null;
				point = point ?? symbols.SequencePoints.FirstOrDefault(p => !p.IsHidden);
				if (point != null)
					view.JumpTo(point.StartLine, point.StartColumn);
			}
			return reference.ToFileName().ToString();
		}
	}
}
