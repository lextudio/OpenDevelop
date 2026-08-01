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
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using ICSharpCode.CodeQuality.Engine.Dom;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.CodeQuality.Engine
{
	/// <summary>
	/// Builds the dependency graph between assemblies, namespaces, types and their members.
	/// Driven by Roslyn symbols (Microsoft.CodeAnalysis) over metadata references, with IL-level
	/// member usage analysis on top of System.Reflection.Metadata (PEReader) - replacing the old
	/// NRefactory.TypeSystem + Mono.Cecil stack.
	/// </summary>
	public class AssemblyAnalyzer
	{
		internal Dictionary<IAssemblySymbol, AssemblyNode> assemblyMappings;
		internal Dictionary<string, NamespaceNode> namespaceMappings;
		internal Dictionary<INamedTypeSymbol, TypeNode> typeMappings;
		internal Dictionary<IMethodSymbol, MethodNode> methodMappings;
		internal Dictionary<IFieldSymbol, FieldNode> fieldMappings;
		internal Dictionary<IPropertySymbol, PropertyNode> propertyMappings;
		internal Dictionary<IEventSymbol, EventNode> eventMappings;
		internal Dictionary<IAssemblySymbol, PEReader> assemblyReaders;
		List<string> fileNames;

		internal IProgressMonitor progressMonitor;

		public AssemblyAnalyzer()
		{
			fileNames = new List<string>();
		}

		public void AddAssemblyFiles(params string[] files)
		{
			fileNames.AddRange(files);
		}

		HashSet<NodeBase> outgoingEdges = new HashSet<NodeBase>();

		public void AddEdge(NodeBase target)
		{
			while (target != null) {
				if (!outgoingEdges.Add(target))
					break;
				target = target.Parent;
			}
		}

		void CreateEdges(NodeBase source)
		{
			while (source != null) {
				foreach (NodeBase n in outgoingEdges) {
					source.AddRelationship(n);
				}
				source = source.Parent;
			}
			outgoingEdges.Clear();
		}

		public ReadOnlyCollection<AssemblyNode> Analyze()
		{
			var loadedAssemblies = LoadAssemblies();

			assemblyMappings = new Dictionary<IAssemblySymbol, AssemblyNode>();
			namespaceMappings = new Dictionary<string, NamespaceNode>();
			typeMappings = new Dictionary<INamedTypeSymbol, TypeNode>();
			fieldMappings = new Dictionary<IFieldSymbol, FieldNode>();
			methodMappings = new Dictionary<IMethodSymbol, MethodNode>();
			propertyMappings = new Dictionary<IPropertySymbol, PropertyNode>();
			eventMappings = new Dictionary<IEventSymbol, EventNode>();

			foreach (var assembly in loadedAssemblies) {
				foreach (var type in GetAllTypeDefinitions(assembly)) {
					var tn = ReadType(type);

					foreach (var field in type.GetMembers().OfType<IFieldSymbol>()) {
						var node = new FieldNode(field);
						fieldMappings.Add(field, node);
						tn.AddChild(node);
					}

					foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary || m.MethodKind == MethodKind.Constructor)) {
						var node = new MethodNode(method);
						methodMappings.Add(method, node);
						tn.AddChild(node);
					}

					foreach (var property in type.GetMembers().OfType<IPropertySymbol>()) {
						var node = new PropertyNode(property);
						propertyMappings.Add(property, node);
						tn.AddChild(node);
					}

					foreach (var @event in type.GetMembers().OfType<IEventSymbol>()) {
						var node = new EventNode(@event);
						eventMappings.Add(@event, node);
						tn.AddChild(node);
					}
				}
			}

			ILAnalyzer analyzer = new ILAnalyzer(this);
			int count = typeMappings.Count + methodMappings.Count + fieldMappings.Count + propertyMappings.Count;
			int i = 0;

			foreach (var element in typeMappings) {
				ReportProgress(++i / (double)count);
				if (element.Key.BaseType != null)
					AddRelationshipsForType(element.Value, element.Key.BaseType);
				AddRelationshipsForTypes(element.Key.Interfaces, element.Value);
				AddRelationshipsForAttributes(element.Key.GetAttributes(), element.Value);
				CreateEdges(element.Value);
			}

			foreach (var element in methodMappings) {
				ReportProgress(++i / (double)count);
				analyzer.Analyze(element.Key, element.Value);
				var node = element.Value;
				var method = element.Key;
				AddRelationshipsForType(node, method.ReturnType);
				AddRelationshipsForAttributes(method.GetAttributes(), node);
				AddRelationshipsForTypeParameters(method.TypeParameters, node);
				foreach (var param in method.Parameters) {
					AddRelationshipsForType(node, param.Type);
					AddRelationshipsForAttributes(param.GetAttributes(), node);
				}
				CreateEdges(element.Value);
			}

			foreach (var element in fieldMappings) {
				ReportProgress(++i / (double)count);
				var node = element.Value;
				var field = element.Key;
				AddRelationshipsForType(node, field.Type);
				AddRelationshipsForAttributes(field.GetAttributes(), node);
				CreateEdges(element.Value);
			}

			foreach (var element in propertyMappings) {
				ReportProgress(++i / (double)count);
				var node = element.Value;
				var property = element.Key;
				if (property.GetMethod != null)
					analyzer.Analyze(property.GetMethod, node);
				if (property.SetMethod != null)
					analyzer.Analyze(property.SetMethod, node);
				AddRelationshipsForType(node, property.Type);
				AddRelationshipsForAttributes(property.GetAttributes(), node);
				CreateEdges(element.Value);
			}

			foreach (var element in eventMappings) {
				ReportProgress(++i / (double)count);
				var node = element.Value;
				var @event = element.Key;
				if (@event.AddMethod != null)
					analyzer.Analyze(@event.AddMethod, node);
				if (@event.RemoveMethod != null)
					analyzer.Analyze(@event.RemoveMethod, node);
				if (@event.RaiseMethod != null)
					analyzer.Analyze(@event.RaiseMethod, node);
				AddRelationshipsForType(node, @event.Type);
				AddRelationshipsForAttributes(@event.GetAttributes(), node);
				CreateEdges(element.Value);
			}

			return new ReadOnlyCollection<AssemblyNode>(assemblyMappings.Values.ToList());
		}

		void ReportProgress(double progress)
		{
			if (progressMonitor != null) {
				progressMonitor.Progress = progress;
			}
		}

		void AddRelationshipsForTypeParameters(ImmutableArray<ITypeParameterSymbol> typeParameters, NodeBase node)
		{
			foreach (var param in typeParameters) {
				AddRelationshipsForAttributes(param.GetAttributes(), node);
			}
		}

		void AddRelationshipsForTypes(IEnumerable<INamedTypeSymbol> directBaseTypes, NodeBase node)
		{
			foreach (var baseType in directBaseTypes) {
				AddRelationshipsForType(node, baseType);
			}
		}

		void AddRelationshipsForAttributes(ImmutableArray<AttributeData> attributes, NodeBase node)
		{
			try {
				foreach (var attr in attributes) {
					if (attr.AttributeConstructor != null) {
						MethodNode target;
						if (methodMappings.TryGetValue(attr.AttributeConstructor, out target))
							AddEdge(target);
					}
				}
			} catch (NotSupportedException nse) {
				LoggingService.DebugFormatted("CQA: Skipping attributes of: {0}\r\nException:\r\n{1}", node.Name, nse);
			}
		}

		void AddRelationshipsForType(NodeBase node, ITypeSymbol type)
		{
			if (type == null)
				return;
			// Strip away generic instantiations / pointers / arrays to get to the underlying type.
			switch (type) {
				case INamedTypeSymbol named:
					TypeNode typeNode;
					if (typeMappings.TryGetValue(named.OriginalDefinition, out typeNode))
						AddEdge(typeNode);
					break;
				case IArrayTypeSymbol array:
					AddRelationshipsForType(node, array.ElementType);
					break;
				case IPointerTypeSymbol pointer:
					AddRelationshipsForType(node, pointer.PointedAtType);
					break;
				case ITypeParameterSymbol tp:
					AddRelationshipsForType(node, tp.ConstraintTypes.FirstOrDefault());
					break;
			}
		}

		IEnumerable<IAssemblySymbol> LoadAssemblies()
		{
			var refs = fileNames.Distinct(StringComparer.OrdinalIgnoreCase)
				.Select(f => MetadataReference.CreateFromFile(f))
				.ToList();
			var compilation = CSharpCompilation.Create("CQA", references: refs);
			var assemblies = compilation.References
				.Select(compilation.GetAssemblyOrModuleSymbol)
				.OfType<IAssemblySymbol>()
				.ToList();

			assemblyReaders = new Dictionary<IAssemblySymbol, PEReader>(SymbolEqualityComparer.Default);
			foreach (var asm in assemblies) {
				var file = fileNames.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(asm.Name, StringComparison.OrdinalIgnoreCase));
				if (file != null) {
					try {
						assemblyReaders[asm] = new PEReader(File.OpenRead(file));
					} catch (IOException) {
					} catch (BadImageFormatException) {
					}
				}
			}
			return assemblies;
		}

		internal PEReader GetReader(IAssemblySymbol assembly)
		{
			PEReader reader;
			if (assemblyReaders != null && assemblyReaders.TryGetValue(assembly, out reader))
				return reader;
			return null;
		}

		internal bool ResolveTokenTarget(int token, NodeBase node)
		{
			// The IL operand is a metadata token; match it against the analyzed assemblies via
			// the SRM reader for the target's declaring assembly (cheap name-based fallback if
			// the token cannot be resolved on the first reader).
			foreach (var reader in assemblyReaders.Values) {
				if (ResolveTokenInReader(reader, token, node))
					return true;
			}
			return false;
		}

		bool ResolveTokenInReader(PEReader reader, int token, NodeBase node)
		{
			var md = reader.GetMetadataReader();
			switch (token >> 24) {
				case 0x06: { // MethodDef
					var handle = MetadataTokens.MethodDefinitionHandle(token);
					if (handle.IsNil)
						return false;
					MethodDefinition methodDef;
					try {
						methodDef = md.GetMethodDefinition(handle);
					} catch (BadImageFormatException) {
						return false;
					}
					string typeKey = GetTypeKey(md, methodDef.GetDeclaringType());
					string methodName = md.GetString(methodDef.Name);
					MethodNode target = methodMappings.Values.FirstOrDefault(m =>
						m.MethodDefinition.Name == methodName && GetTypeKey(m.MethodDefinition.ContainingType) == typeKey);
					if (target != null) {
						AddEdge(target);
						return true;
					}
					break;
				}
				case 0x04: { // FieldDef
					var handle = MetadataTokens.FieldDefinitionHandle(token);
					if (handle.IsNil)
						return false;
					FieldDefinition fieldDef;
					try {
						fieldDef = md.GetFieldDefinition(handle);
					} catch (BadImageFormatException) {
						return false;
					}
					string typeKey = GetTypeKey(md, fieldDef.GetDeclaringType());
					string fieldName = md.GetString(fieldDef.Name);
					FieldNode target = fieldMappings.Values.FirstOrDefault(f =>
						f.FieldDefinition.Name == fieldName && GetTypeKey(f.FieldDefinition.ContainingType) == typeKey);
					if (target != null) {
						AddEdge(target);
						return true;
					}
					break;
				}
			}
			return false;
		}

		/// <summary>
		/// Gets method bodies for a symbol: (type key, method name) match against the
		/// per-assembly RVA index built from the PE files.
		/// </summary>
		internal IEnumerable<MethodBodyBlock> GetMethodBodies(IMethodSymbol method)
		{
			var reader = GetReader(method.ContainingAssembly);
			if (reader == null)
				yield break;
			string typeKey = GetTypeKey(method.ContainingType);
			foreach (var handle in reader.GetMetadataReader().MethodDefinitions) {
				MethodDefinition def;
				try {
					def = reader.GetMetadataReader().GetMethodDefinition(handle);
				} catch (BadImageFormatException) {
					continue;
				}
				if (reader.GetMetadataReader().GetString(def.Name) != method.Name)
					continue;
				if (GetTypeKey(reader.GetMetadataReader(), def.GetDeclaringType()) != typeKey)
					continue;
				int rva = def.RelativeVirtualAddress;
				if (rva == 0)
					continue;
				MethodBodyBlock body;
				try {
					body = reader.GetMethodBody(rva);
				} catch (BadImageFormatException) {
					continue;
				}
				if (body != null)
					yield return body;
			}
		}

		// Type identity key used on both sides of the metadata boundary: fully qualified name
		// with nested types joined by '.', generic arity stripped (MetadataName never includes
		// type arguments), namespace only on the outermost type - mirrors GetTypeKey(MetadataReader).
		static string GetTypeKey(ITypeSymbol type)
		{
			if (type.ContainingType != null)
				return GetTypeKey(type.ContainingType) + "." + type.MetadataName;
			var ns = type.ContainingNamespace != null && !type.ContainingNamespace.IsGlobalNamespace
				? type.ContainingNamespace.ToDisplayString()
				: string.Empty;
			return string.IsNullOrEmpty(ns) ? type.MetadataName : ns + "." + type.MetadataName;
		}

		static string GetTypeKey(MetadataReader md, TypeDefinitionHandle handle)
		{
			try {
				var typeDef = md.GetTypeDefinition(handle);
				string name = md.GetString(typeDef.Name);
				if (!typeDef.GetDeclaringType().IsNil)
					return GetTypeKey(md, typeDef.GetDeclaringType()) + "." + name;
				string ns = md.GetString(typeDef.Namespace);
				return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
			} catch (BadImageFormatException) {
				return string.Empty;
			}
		}

		NamespaceNode GetOrCreateNamespace(AssemblyNode assembly, string namespaceName)
		{
			NamespaceNode result;
			if (!namespaceMappings.TryGetValue(namespaceName + "," + assembly.Name, out result)) {
				result = new NamespaceNode(namespaceName);
				assembly.AddChild(result);
				namespaceMappings.Add(namespaceName + "," + assembly.Name, result);
			}
			return result;
		}

		AssemblyNode GetOrCreateAssembly(IAssemblySymbol asm)
		{
			AssemblyNode result;
			if (!assemblyMappings.TryGetValue(asm, out result)) {
				result = new AssemblyNode(asm);
				assemblyMappings.Add(asm, result);
			}
			return result;
		}

		TypeNode ReadType(INamedTypeSymbol type)
		{
			var asm = GetOrCreateAssembly(type.ContainingAssembly);
			var ns = GetOrCreateNamespace(asm, type.ContainingNamespace?.ToDisplayString() ?? string.Empty);
			TypeNode parent;
			var node = new TypeNode(type);
			if (type.ContainingType != null) {
				if (typeMappings.TryGetValue(type.ContainingType, out parent))
					parent.AddChild(node);
				else
					throw new Exception("TypeNode not found: " + type.ContainingType.ToDisplayString());
			} else
				ns.AddChild(node);
			typeMappings.Add(type, node);
			return node;
		}

		static IEnumerable<INamedTypeSymbol> GetAllTypeDefinitions(IAssemblySymbol assembly)
		{
			var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
			return GetAllTypes(assembly.GlobalNamespace, visited);
		}

		static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns, HashSet<INamedTypeSymbol> visited)
		{
			foreach (var type in ns.GetTypeMembers()) {
				foreach (var t in EnumerateTypeAndNested(type, visited))
					yield return t;
			}
			foreach (var nestedNs in ns.GetNamespaceMembers()) {
				foreach (var t in GetAllTypes(nestedNs, visited))
					yield return t;
			}
		}

		static IEnumerable<INamedTypeSymbol> EnumerateTypeAndNested(INamedTypeSymbol type, HashSet<INamedTypeSymbol> visited)
		{
			if (!visited.Add(type))
				yield break;
			yield return type;
			foreach (var nested in type.GetTypeMembers()) {
				foreach (var t in EnumerateTypeAndNested(nested, visited))
					yield return t;
			}
		}
	}
}
