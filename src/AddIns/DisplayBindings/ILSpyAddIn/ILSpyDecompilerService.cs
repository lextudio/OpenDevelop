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
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using System.Threading;

using ICSharpCode.Core;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.SharpDevelop;
using ICSharpCode.TypeSystem;

using DecompilerFullTypeName = ICSharpCode.Decompiler.TypeSystem.FullTypeName;
using DecompilerIEntity = ICSharpCode.Decompiler.TypeSystem.IEntity;
using DecompilerIParameterizedMember = ICSharpCode.Decompiler.TypeSystem.IParameterizedMember;
using OpenDevelopTextLocation = ICSharpCode.TypeSystem.TextLocation;

namespace ICSharpCode.ILSpyAddIn
{
	public sealed class DecompiledTypeResult
	{
		public DecompiledTypeResult(string output, IReadOnlyDictionary<string, OpenDevelopTextLocation> memberLocations)
			: this(output, memberLocations, new Dictionary<string, DecompiledMethodDebugInfo>())
		{
		}
		
		public DecompiledTypeResult(string output, IReadOnlyDictionary<string, OpenDevelopTextLocation> memberLocations, IReadOnlyDictionary<string, DecompiledMethodDebugInfo> debugSymbols)
		{
			Output = output ?? throw new ArgumentNullException("output");
			MemberLocations = memberLocations ?? throw new ArgumentNullException("memberLocations");
			DebugSymbols = debugSymbols ?? throw new ArgumentNullException("debugSymbols");
		}
		
		public string Output { get; private set; }
		public IReadOnlyDictionary<string, OpenDevelopTextLocation> MemberLocations { get; private set; }
		public IReadOnlyDictionary<string, DecompiledMethodDebugInfo> DebugSymbols { get; private set; }
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
				var decompiler = new CSharpDecompiler(name.AssemblyFile, settings);
				var syntaxTree = name.IsWholeModule
					? decompiler.DecompileWholeModuleAsSingleFile()
					: decompiler.DecompileType(new DecompilerFullTypeName(name.Type.ReflectionName));
				return WriteSyntaxTree(syntaxTree, settings, CreateDebugSymbols(decompiler, syntaxTree));
			}
		}
		
		static DecompiledTypeResult WriteSyntaxTree(SyntaxTree syntaxTree, DecompilerSettings settings, IReadOnlyDictionary<string, DecompiledMethodDebugInfo> debugSymbols)
		{
			StringWriter writer = new StringWriter();
			var tokenWriter = new MemberLocationTokenWriter(new TextWriterTokenWriter(writer));
			syntaxTree.AcceptVisitor(new CSharpOutputVisitor(tokenWriter, settings.CSharpFormattingOptions));
			return new DecompiledTypeResult(writer.ToString(), tokenWriter.MemberLocations, debugSymbols);
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
		
		sealed class MemberLocationTokenWriter : DecoratingTokenWriter
		{
			readonly TextWriterTokenWriter locationProvider;
			readonly Dictionary<string, OpenDevelopTextLocation> memberLocations = new Dictionary<string, OpenDevelopTextLocation>();
			
			public MemberLocationTokenWriter(TextWriterTokenWriter writer)
				: base(writer)
			{
				locationProvider = writer;
			}
			
			public IReadOnlyDictionary<string, OpenDevelopTextLocation> MemberLocations {
				get { return memberLocations; }
			}
			
			public override void StartNode(AstNode node)
			{
				base.StartNode(node);
				var symbol = node.GetSymbol() as DecompilerIEntity;
				string key = MemberLocationKey.Create(symbol);
				if (key == null)
					return;
				
				if (!memberLocations.ContainsKey(key)) {
					var location = locationProvider.Location;
					memberLocations.Add(key, new OpenDevelopTextLocation(location.Line, location.Column));
				}
			}
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
