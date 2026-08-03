// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using ICSharpCode.Core;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.SharpDevelop;
using ICSharpCode.TypeSystem;

using DecompilerFullTypeName = ICSharpCode.Decompiler.TypeSystem.FullTypeName;
using DecompilerIEntity = ICSharpCode.Decompiler.TypeSystem.IEntity;
using DecompilerIMember = ICSharpCode.Decompiler.TypeSystem.IMember;
using DecompilerIParameterizedMember = ICSharpCode.Decompiler.TypeSystem.IParameterizedMember;
using DecompilerIType = ICSharpCode.Decompiler.TypeSystem.IType;
using OpenDevelopTextLocation = ICSharpCode.TypeSystem.TextLocation;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class DecompiledTypeResult
	{
		public DecompiledTypeResult(string output, IReadOnlyDictionary<string, OpenDevelopTextLocation> memberLocations)
			: this(output, memberLocations, new Dictionary<string, DecompiledMethodDebugInfo>(), Array.Empty<DecompiledReferenceSpan>())
		{
		}

		public DecompiledTypeResult(string output, IReadOnlyDictionary<string, OpenDevelopTextLocation> memberLocations, IReadOnlyDictionary<string, DecompiledMethodDebugInfo> debugSymbols)
			: this(output, memberLocations, debugSymbols, Array.Empty<DecompiledReferenceSpan>())
		{
		}

		public DecompiledTypeResult(string output, IReadOnlyDictionary<string, OpenDevelopTextLocation> memberLocations, IReadOnlyDictionary<string, DecompiledMethodDebugInfo> debugSymbols, IReadOnlyList<DecompiledReferenceSpan> references)
		{
			Output = output ?? throw new ArgumentNullException("output");
			MemberLocations = memberLocations ?? throw new ArgumentNullException("memberLocations");
			DebugSymbols = debugSymbols ?? throw new ArgumentNullException("debugSymbols");
			References = references ?? throw new ArgumentNullException("references");
		}

		public string Output { get; private set; }
		public IReadOnlyDictionary<string, OpenDevelopTextLocation> MemberLocations { get; private set; }
		public IReadOnlyDictionary<string, DecompiledMethodDebugInfo> DebugSymbols { get; private set; }

		/// <summary>
		/// Clickable type/member reference occurrences within <see cref="Output"/> (doc/technotes/
		/// ilspy.md "Unify C# document hosting" - reference hyperlink navigation) - every
		/// use-site (not just definitions), restricted to entities declared in the assembly being
		/// decompiled (see <see cref="DecompiledReferenceSpan"/>'s own doc comment for why).
		/// </summary>
		public IReadOnlyList<DecompiledReferenceSpan> References { get; private set; }

		/// <summary>
		/// Returns a copy carrying <paramref name="debugSymbols"/> - lets the decompile pipeline
		/// render display text from the pristine AST before the location/sequence-point pass mutates
		/// it (see ILSpyDecompilerService.DecompileType's "ORDER MATTERS" comment).
		/// </summary>
		internal DecompiledTypeResult WithDebugSymbols(IReadOnlyDictionary<string, DecompiledMethodDebugInfo> debugSymbols)
		{
			return new DecompiledTypeResult(Output, MemberLocations, debugSymbols, References);
		}
	}

	/// <summary>
	/// One clickable type/member reference occurrence in a <see cref="DecompiledTypeResult"/>'s
	/// <see cref="DecompiledTypeResult.Output"/> - <see cref="Offset"/>/<see cref="Length"/> are
	/// raw character offsets into that exact string. Restricted to entities whose declaring
	/// module is the same module being decompiled (an external reference, e.g. `System.String`,
	/// would need resolving and opening a *different* assembly's document - out of scope for this
	/// pass; only same-assembly cross-type/cross-member navigation is captured).
	/// </summary>
	public sealed class DecompiledReferenceSpan
	{
		public DecompiledReferenceSpan(int offset, int length, string topLevelTypeReflectionName, string memberKey)
		{
			Offset = offset;
			Length = length;
			TopLevelTypeReflectionName = topLevelTypeReflectionName ?? throw new ArgumentNullException("topLevelTypeReflectionName");
			MemberKey = memberKey;
		}

		public int Offset { get; }
		public int Length { get; }
		/// <summary>The reference target's top-level declaring type, to resolve via <see cref="DecompiledTypeReference"/>.</summary>
		public string TopLevelTypeReflectionName { get; }
		/// <summary>Null when the reference is to the type itself (navigate to the type, not a member within it).</summary>
		public string MemberKey { get; }
	}
	
	public sealed class DecompiledMethodDebugInfo
	{
		public DecompiledMethodDebugInfo(uint methodDefToken, IReadOnlyList<ICSharpCode.Decompiler.DebugInfo.SequencePoint> sequencePoints)
		{
			MethodDefToken = methodDefToken;
			SequencePoints = sequencePoints ?? throw new ArgumentNullException("sequencePoints");
		}
		
		public uint MethodDefToken { get; private set; }
		public IReadOnlyList<ICSharpCode.Decompiler.DebugInfo.SequencePoint> SequencePoints { get; private set; }
	}
	
	public static class ILSpyDecompilerService
	{
		public static DecompiledTypeResult DecompileType(DecompiledTypeReference name, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (name == null)
				throw new ArgumentNullException("name");
			if (name.AssemblyFile == null || !SD.FileSystem.FileExists(name.AssemblyFile))
				throw new InvalidOperationException("Could not find assembly file");

			using (DebugTimer.Time("DecompileType: " + name.ToFileName())) {
				cancellationToken.ThrowIfCancellationRequested();
				var settings = new DecompilerSettings();
				var decompiler = CreateDecompiler(name.AssemblyFile, settings);
				var syntaxTree = name.IsWholeModule
					? decompiler.DecompileWholeModuleAsSingleFile()
					: decompiler.DecompileType(new DecompilerFullTypeName(name.Type.ReflectionName));

				// ROOT CAUSE FIX (2026-08-03) for the "missing startLocation" Debug.Assert storm out
				// of ICSharpCode.Decompiler.CSharp.SequencePointBuilder.EndSequencePoint:
				// CreateSequencePoints reads node.StartLocation/EndLocation off the AST, but a
				// *decompiled* AST is synthesized, never parsed, so every node's location is
				// TextLocation.Empty until the tree has been rendered through a token writer wrapped
				// in TokenWriter.WrapInWriterThatSetsLocationsInAST (an InsertMissingTokensDecorator,
				// which populates locations - and inserts the implicit tokens - as it writes). Both
				// upstream callers of CreateSequencePoints do exactly this render-then-compute
				// sequence first: ICSharpCode.Decompiler/DebugInfo/PortablePdbWriter.cs's
				// SyntaxTreeToString + line ~126, and ILSpy/Languages/CSharpILMixedLanguage.cs's
				// WriteCode + line ~105. This service did neither, so the assert fired for
				// essentially every statement it decompiled. (It was also calling CreateDebugSymbols
				// in an argument position, i.e. evaluated *before* WriteSyntaxTree's own render pass
				// - so even an incidental location side effect could never have helped.)
				//
				// ORDER MATTERS, and not in the obvious way: InsertMissingTokensDecorator *mutates*
				// the AST (it inserts the implicit tokens whose locations it is recording), and that
				// mutation is visible in anything rendered from the tree afterwards - measured, not
				// assumed: rendering display text after the location pass moved a comment out of an
				// attribute's argument list (`DebuggerBrowsable(/*Could not decode...*/)` became
				// `/*Could not decode...*/DebuggerBrowsable()`) and changed the output length. That
				// is upstream's *mixed IL/C#* rendering behavior (CSharpILMixedLanguage.WriteCode
				// wraps, so it displays the mutated tree), but NOT upstream's plain C# view
				// (CSharpLanguage.WriteCode does not wrap) - which is what this document is. So:
				// render the display text from the pristine tree FIRST, and only then mutate it for
				// the debug-symbol pass. DebugSymbols is live - ILSpySymbolSource.cs feeds it to the
				// debugger for stepping into decompiled code - so it cannot simply be dropped.
				var result = WriteSyntaxTree(syntaxTree, settings, decompiler);
				SetLocationsInAst(syntaxTree, settings);
				return result.WithDebugSymbols(CreateDebugSymbols(decompiler, syntaxTree));
			}
		}

		// A bare `new CSharpDecompiler(fileName, settings)` builds its own from-scratch
		// UniversalAssemblyResolver with no target-framework/search-path context, so it fails to
		// resolve framework references (e.g. "Failed to resolve assembly: System.Runtime") for any
		// assembly beyond a trivial self-contained one - confirmed live while wiring up
		// doc/technotes/ilspy.md "Unify C# document hosting" phase 1. The tree-driven decompile path
		// (AssemblyTreeModel/DecompilerTextView) never hits this because it always decompiles
		// through an already-loaded ICSharpCode.ILSpyX.LoadedAssembly, whose
		// GetAssemblyResolver()/GetMetadataFileOrNull() already carry that context. Reuse the same
		// already-loaded LoadedAssembly (found in the hosted ILSpy AssemblyList by file path) when
		// one exists - which it always does for anything reached through the assembly tree - falling
		// back to the old bare constructor only when the file isn't loaded there (e.g. this service
		// used standalone, outside the tree-hosted workflow).
		static CSharpDecompiler CreateDecompiler(FileName assemblyFile, DecompilerSettings settings)
		{
			var loadedAssembly = IlSpyWorkspaceHost.IsInitialized
				? IlSpyWorkspaceHost.AssemblyTreeModel.AssemblyList.GetAssemblies()
					.FirstOrDefault(a => string.Equals(a.FileName, assemblyFile.ToString(), StringComparison.OrdinalIgnoreCase))
				: null;
			var module = loadedAssembly?.GetMetadataFileOrNull();
			if (module == null)
				return new CSharpDecompiler(assemblyFile, settings);
			return new CSharpDecompiler(module, loadedAssembly.GetAssemblyResolver(), settings);
		}
		
		// Populates StartLocation/EndLocation on every node of a synthesized (decompiled) AST, which
		// CreateSequencePoints requires - see the call site's comment for the full root-cause story.
		// Mirrors PortablePdbWriter.SyntaxTreeToString: the rendered text is deliberately discarded,
		// only the AST mutation (locations + implicit tokens) matters. WrapInWriterThatSetsLocationsInAST
		// demands an ILocatable writer, which is why this uses TextWriterTokenWriter rather than the
		// TextTokenWriter the display pass below needs for reference capture.
		static void SetLocationsInAst(SyntaxTree syntaxTree, DecompilerSettings settings)
		{
			var throwaway = new StringWriter();
			TokenWriter tokenWriter = new TextWriterTokenWriter(throwaway) {
				IndentationString = settings.CSharpFormattingOptions.IndentationString
			};
			tokenWriter = TokenWriter.WrapInWriterThatSetsLocationsInAST(tokenWriter);
			syntaxTree.AcceptVisitor(new CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions));
		}

		// Uses TextTokenWriter (real ILSpy's own rich-output token writer, normally paired with
		// AvalonEditTextOutput for the bespoke DecompilerTextView pane) instead of the plain
		// TextWriterTokenWriter this used before, so that every type/member reference - not just
		// definitions - flows through ITextOutput.WriteReference and can be captured by offset
		// (doc/technotes/ilspy.md "Unify C# document hosting" - reference hyperlink navigation).
		// ReferenceTrackingTextOutput below reimplements plain-text writing itself (mirroring
		// ICSharpCode.Decompiler.PlainTextOutput, which is sealed and can't be subclassed) so this
		// is a single pass producing text + member locations + reference spans together, rather
		// than risking a second pass with a different writer producing subtly different
		// formatting and desynchronized offsets.
		static DecompiledTypeResult WriteSyntaxTree(SyntaxTree syntaxTree, DecompilerSettings settings, CSharpDecompiler decompiler)
		{
			var output = new ReferenceTrackingTextOutput(decompiler.TypeSystem.MainModule);
			output.IndentationString = settings.CSharpFormattingOptions.IndentationString;
			var tokenWriter = new TextTokenWriter(output, settings, decompiler.TypeSystem);
			syntaxTree.AcceptVisitor(new CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions));
			return new DecompiledTypeResult(output.ToString(), output.MemberLocations,
				new Dictionary<string, DecompiledMethodDebugInfo>(), output.References);
		}

		// Reimplements ICSharpCode.Decompiler.PlainTextOutput (sealed, can't be subclassed) with
		// two additions: WriteReference(IMember, ..., isDefinition: true) records a member's
		// declaration location (replacing the old MemberLocationTokenWriter's StartNode-based,
		// first-occurrence-only heuristic with the precise, single-source-of-truth location
		// TextTokenWriter already resolves for every identifier), and every reference - definition
		// or not, type or member - restricted to entities declared in the module being decompiled
		// (see DecompiledReferenceSpan's doc comment) is recorded by raw character offset for
		// click-to-navigate.
		sealed class ReferenceTrackingTextOutput : ITextOutput
		{
			readonly StringBuilder sb = new StringBuilder();
			readonly Decompiler.TypeSystem.IModule mainModule;
			readonly Dictionary<string, OpenDevelopTextLocation> memberLocations = new Dictionary<string, OpenDevelopTextLocation>();
			readonly List<DecompiledReferenceSpan> references = new List<DecompiledReferenceSpan>();
			int indent;
			bool needsIndent;
			int line = 1, column = 1;

			public ReferenceTrackingTextOutput(Decompiler.TypeSystem.IModule mainModule)
			{
				this.mainModule = mainModule;
			}

			public string IndentationString { get; set; } = "\t";
			public IReadOnlyDictionary<string, OpenDevelopTextLocation> MemberLocations => memberLocations;
			public IReadOnlyList<DecompiledReferenceSpan> References => references;

			public override string ToString() => sb.ToString();

			public void Indent() => indent++;
			public void Unindent() => indent--;

			void WriteIndentIfNeeded()
			{
				if (needsIndent) {
					needsIndent = false;
					for (int i = 0; i < indent; i++)
						sb.Append(IndentationString);
					column += indent;
				}
			}

			public void Write(char ch)
			{
				WriteIndentIfNeeded();
				sb.Append(ch);
				column++;
			}

			public void Write(string text)
			{
				WriteIndentIfNeeded();
				sb.Append(text);
				column += text.Length;
			}

			public void WriteLine()
			{
				sb.Append(Environment.NewLine);
				needsIndent = true;
				line++;
				column = 1;
			}

			public void WriteReference(OpCodeInfo opCode, bool omitSuffix = false) => Write(opCode.Name);

			public void WriteReference(MetadataFile metadata, Handle handle, string text, string protocol = "decompile", bool isDefinition = false) => Write(text);

			public void WriteReference(DecompilerIType type, string text, bool isDefinition = false)
			{
				RecordAndWrite(text, type.GetDefinition() as DecompilerIEntity, isDefinition);
			}

			public void WriteReference(DecompilerIMember member, string text, bool isDefinition = false)
			{
				RecordAndWrite(text, member, isDefinition);
			}

			void RecordAndWrite(string text, DecompilerIEntity entity, bool isDefinition)
			{
				WriteIndentIfNeeded();
				int offset = sb.Length;
				int startLine = line, startColumn = column;
				sb.Append(text);
				column += text.Length;

				if (entity == null)
					return;

				// Definition location (replaces the old MemberLocationTokenWriter.StartNode
				// heuristic) - captured at the START of the identifier, matching what
				// JumpToMember's TextLocation.Line/Column expects (AvalonEdit's JumpTo).
				if (isDefinition) {
					string definitionKey = MemberLocationKey.Create(entity);
					if (definitionKey != null && !memberLocations.ContainsKey(definitionKey))
						memberLocations.Add(definitionKey, new OpenDevelopTextLocation(startLine, startColumn));
				}

				// Only same-module entities: an external reference (e.g. System.String) would need
				// resolving and opening a *different* assembly's document - out of scope for this
				// pass (see DecompiledReferenceSpan's doc comment).
				if (!ReferenceEquals(entity.ParentModule, mainModule))
					return;
				var declaringType = entity as ICSharpCode.Decompiler.TypeSystem.ITypeDefinition ?? entity.DeclaringTypeDefinition;
				while (declaringType?.DeclaringTypeDefinition != null)
					declaringType = declaringType.DeclaringTypeDefinition;
				if (declaringType == null)
					return;
				string memberKey = entity is ICSharpCode.Decompiler.TypeSystem.ITypeDefinition ? null : MemberLocationKey.Create(entity);
				references.Add(new DecompiledReferenceSpan(offset, text.Length, declaringType.ReflectionName, memberKey));
			}

			public void WriteLocalReference(string text, object reference, bool isDefinition = false) => Write(text);
			public void MarkFoldStart(string collapsedText = "...", bool defaultCollapsed = false, bool isDefinition = false) { }
			public void MarkFoldEnd() { }
		}
		
		static IReadOnlyDictionary<string, DecompiledMethodDebugInfo> CreateDebugSymbols(CSharpDecompiler decompiler, SyntaxTree syntaxTree)
		{
			var result = new Dictionary<string, DecompiledMethodDebugInfo>();
			foreach (var item in decompiler.CreateSequencePoints(syntaxTree)) {
				ILFunction function = item.Key;
				if (function.Method == null || function.Method.MetadataToken.IsNil)
					continue;
				string key = MemberLocationKey.Create(function.Method);
				if (key == null)
					continue;
				result[key] = new DecompiledMethodDebugInfo(
					(uint)MetadataTokens.GetToken(function.Method.MetadataToken),
					item.Value);
			}
			return result;
		}
		
	}
	
	public static class MemberLocationKey
	{
		public static string Create(IEntity entity)
		{
			if (entity == null)
				return null;
			var declaringType = entity.DeclaringTypeDefinition;
			if (declaringType == null) {
				return "type|" + entity.ReflectionName;
			}
			return entity.SymbolKind + "|" + declaringType.ReflectionName + "|" + entity.Name + "|" + GetParameterCount(entity);
		}
		
		static int GetParameterCount(IEntity entity)
		{
			var parameterizedMember = entity as IParameterizedMember;
			return parameterizedMember != null ? parameterizedMember.Parameters.Count : -1;
		}
		
		public static string Create(DecompilerIEntity entity)
		{
			if (entity == null)
				return null;
			var declaringType = entity.DeclaringTypeDefinition;
			if (declaringType == null) {
				return "type|" + entity.ReflectionName;
			}
			return entity.SymbolKind + "|" + declaringType.ReflectionName + "|" + entity.Name + "|" + GetParameterCount(entity);
		}
		
		static int GetParameterCount(DecompilerIEntity entity)
		{
			var parameterizedMember = entity as DecompilerIParameterizedMember;
			return parameterizedMember != null ? parameterizedMember.Parameters.Count : -1;
		}
	}
	
	public class DecompiledTypeReference : IEquatable<DecompiledTypeReference>
	{
		public FileName AssemblyFile { get; private set; }
		public TopLevelTypeName Type { get; private set; }
		
		public bool IsWholeModule {
			get { return string.IsNullOrEmpty(Type.Name); }
		}
		
		public DecompiledTypeReference(FileName assemblyFile, TopLevelTypeName type)
		{
			this.AssemblyFile = assemblyFile;
			this.Type = type;
		}
		
		public FileName ToFileName()
		{
			return FileName.Create("ilspy://" + AssemblyFile + "/" + (IsWholeModule ? "module" : EscapeTypeName(Type.ReflectionName)) + ".cs");
		}
		
		static readonly Regex nameRegex = new Regex(@"^ilspy\://(.+)/(.+)\.cs$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		
		public static DecompiledTypeReference FromFileName(string filename)
		{
			var match = nameRegex.Match(filename);
			if (!match.Success) return null;
			
			string asm, typeName;
			asm = match.Groups[1].Value;
			typeName = match.Groups[2].Value;
			if (string.Equals(typeName, "module", StringComparison.OrdinalIgnoreCase))
				return new DecompiledTypeReference(new FileName(asm), default(TopLevelTypeName));
			typeName = UnescapeTypeName(typeName);
			
			return new DecompiledTypeReference(new FileName(asm), new TopLevelTypeName(typeName));
		}
		
		public static DecompiledTypeReference FromTypeDefinition(ITypeDefinition definition)
		{
			FileName assemblyLocation = definition.ParentAssembly.GetRuntimeAssemblyLocation();
			if (assemblyLocation != null && SD.FileSystem.FileExists(assemblyLocation)) {
				return new DecompiledTypeReference(assemblyLocation, definition.FullTypeName.TopLevelTypeName);
			}
			return null;
		}
		
		public static string EscapeTypeName(string typeName)
		{
			if (typeName == null)
				throw new ArgumentNullException("typeName");
			foreach (var ch in new[] { '_' }.Concat(Path.GetInvalidFileNameChars())) {
				typeName = typeName.Replace(ch.ToString(), string.Format("_{0:X4}", (int)ch));
			}
			return typeName;
		}
		
		static readonly Regex unescapeRegex = new Regex(@"_([0-9A-F]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		
		public static string UnescapeTypeName(string typeName)
		{
			if (typeName == null)
				throw new ArgumentNullException("typeName");
			typeName = unescapeRegex.Replace(typeName, m => ((char)int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber)).ToString());
			return typeName;
		}
		
		public override bool Equals(object obj)
		{
			DecompiledTypeReference other = (DecompiledTypeReference)obj;
			if (other == null)
				return false;
			return Equals(other);
		}
		
		public bool Equals(DecompiledTypeReference other)
		{
			return object.Equals(this.AssemblyFile, other.AssemblyFile) && this.Type == other.Type;
		}
		
		public override int GetHashCode()
		{
			int hashCode = 0;
			unchecked {
				if (AssemblyFile != null)
					hashCode += 1000000007 * AssemblyFile.GetHashCode();
				hashCode += 1000000009 * Type.GetHashCode();
			}
			return hashCode;
		}
	}
}
