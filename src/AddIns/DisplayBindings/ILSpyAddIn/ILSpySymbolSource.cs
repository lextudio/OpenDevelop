// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.Generic;
using System.Linq;

using Debugger;
using ICSharpCode.Core;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class ILSpySymbolSource : ISymbolSource
	{
		public bool Handles(IMethod method)
		{
			return method != null && !SD.Debugger.Options.EnableJustMyCode;
		}
		
		public bool IsCompilerGenerated(IMethod method)
		{
			return false;
		}
		
		public SequencePoint GetSequencePoint(IMethod method, int iloffset)
		{
			var symbols = GetSymbols(method);
			if (symbols == null)
				return null;
			
			var sequencePoint = symbols.SequencePoints
				.FirstOrDefault(p => p.Offset <= iloffset && iloffset < p.EndOffset)
				?? symbols.SequencePoints.FirstOrDefault(p => iloffset <= p.Offset);
			return sequencePoint != null ? ToDebugger(sequencePoint, symbols.MethodDefToken, GetFileName(method)) : null;
		}
		
		public IEnumerable<SequencePoint> GetSequencePoints(Debugger.Module module, string filename, int line, int column)
		{
			var name = DecompiledTypeReference.FromFileName(filename);
			if (name == null || module == null || !FileUtility.IsEqualFileName(module.FullPath, name.AssemblyFile))
				yield break;
			
			var parseInfo = SD.ParserService.Parse(name.ToFileName()) as ILSpyParseInformation;
			if (parseInfo == null)
				yield break;
			
			foreach (var symbols in parseInfo.Result.DebugSymbols.Values) {
				foreach (var point in symbols.SequencePoints) {
					if (ContainsLocation(point, line, column))
						yield return ToDebugger(point, symbols.MethodDefToken, filename);
				}
			}
		}
		
		public IEnumerable<ILRange> GetIgnoredILRanges(IMethod method)
		{
			return Enumerable.Empty<ILRange>();
		}
		
		public IEnumerable<ILLocalVariable> GetLocalVariables(IMethod method)
		{
			return Enumerable.Empty<ILLocalVariable>();
		}
		
		static DecompiledMethodDebugInfo GetSymbols(IMethod method)
		{
			if (method == null)
				return null;
			var typeName = DecompiledTypeReference.FromTypeDefinition(method.DeclaringTypeDefinition);
			if (typeName == null)
				return null;
			
			var parseInfo = SD.ParserService.Parse(typeName.ToFileName()) as ILSpyParseInformation;
			if (parseInfo == null)
				return null;
			
			DecompiledMethodDebugInfo symbols;
			return parseInfo.Result.DebugSymbols.TryGetValue(MemberLocationKey.Create(method.MemberDefinition), out symbols) ? symbols : null;
		}
		
		static string GetFileName(IMethod method)
		{
			var typeName = DecompiledTypeReference.FromTypeDefinition(method.DeclaringTypeDefinition);
			return typeName != null ? typeName.ToFileName() : null;
		}
		
		static bool ContainsLocation(ICSharpCode.Decompiler.DebugInfo.SequencePoint point, int line, int column)
		{
			if (point == null || point.IsHidden)
				return false;
			if (column == 0)
				return line >= point.StartLine && line <= point.EndLine;
			return (point.StartLine < line || (point.StartLine == line && point.StartColumn <= column))
				&& (line < point.EndLine || (line == point.EndLine && column <= point.EndColumn));
		}
		
		static SequencePoint ToDebugger(ICSharpCode.Decompiler.DebugInfo.SequencePoint point, uint methodDefToken, string filename)
		{
			return new SequencePoint {
				MethodDefToken = methodDefToken,
				ILRanges = new[] { new ILRange(point.Offset, point.EndOffset) },
				Filename = filename,
				StartLine = point.StartLine,
				StartColumn = point.StartColumn,
				EndLine = point.EndLine,
				EndColumn = point.EndColumn
			};
		}
	}
}
