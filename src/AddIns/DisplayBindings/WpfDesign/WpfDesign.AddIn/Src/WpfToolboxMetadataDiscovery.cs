// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>Reads candidate WPF controls from PE metadata without loading a project or a
	/// third-party assembly into the IDE. The actual type resolution and construction remain in the
	/// disposable design child, which is the only process allowed to execute project code.</summary>
	static class WpfToolboxMetadataDiscovery
	{
		internal sealed class ControlInfo
		{
			public string Name { get; init; } = "";
			public string Namespace { get; init; } = "";
			public string AssemblyName { get; init; } = "";
			public bool IsUserControl { get; init; }
			public List<DesignerToolboxFilterInfo> Filters { get; init; } = new();
		}

		sealed class AssemblyImage : IDisposable
		{
			readonly FileStream stream;
			readonly PEReader pe;
			public MetadataReader Metadata { get; }
			public string Path { get; }
			public string Name { get; }

			public AssemblyImage(string path)
			{
				Path = path;
				stream = File.OpenRead(path);
				pe = new PEReader(stream);
				if (!pe.HasMetadata)
					throw new BadImageFormatException("The file has no CLR metadata.", path);
				Metadata = pe.GetMetadataReader();
				Name = Metadata.GetString(Metadata.GetAssemblyDefinition().Name);
			}

			public void Dispose()
			{
				pe.Dispose();
				stream.Dispose();
			}
		}

		/// <summary>Discovers public, concrete classes whose metadata base chain reaches a WPF
		/// visual base. <paramref name="referencePaths"/> is used only to follow metadata TypeRefs;
		/// none of those files is ever loaded into an AssemblyLoadContext.</summary>
		public static IReadOnlyList<ControlInfo> Discover(string assemblyPath, IEnumerable<string> referencePaths)
		{
			var pathsByAssemblyName = referencePaths
				.Where(File.Exists)
				.GroupBy(path => System.IO.Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
				.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
			var images = new Dictionary<string, AssemblyImage>(StringComparer.OrdinalIgnoreCase);
			try {
				var root = Open(assemblyPath, images);
				var result = new List<ControlInfo>();
				foreach (var handle in root.Metadata.TypeDefinitions) {
					var definition = root.Metadata.GetTypeDefinition(handle);
				if (!IsPublicTopLevel(definition) || (definition.Attributes & TypeAttributes.Abstract) != 0
					|| (definition.Attributes & TypeAttributes.SpecialName) != 0
					|| root.Metadata.GetString(definition.Name).Contains('`'))
						continue;
					if (!IsWpfVisualBase(root, definition.BaseType, pathsByAssemblyName, images, new HashSet<string>(StringComparer.Ordinal)))
						continue;
					var name = root.Metadata.GetString(definition.Name);
					var @namespace = root.Metadata.GetString(definition.Namespace);
					if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(@namespace))
						continue;
					result.Add(new ControlInfo {
						Name = name,
						Namespace = @namespace,
						AssemblyName = root.Name,
						IsUserControl = IsUserControlBase(root, definition.BaseType, pathsByAssemblyName, images, new HashSet<string>(StringComparer.Ordinal)),
						Filters = ReadToolboxFilters(root.Metadata, definition)
					});
				}
				return result;
			} finally {
				foreach (var image in images.Values)
					image.Dispose();
			}
		}

		static List<DesignerToolboxFilterInfo> ReadToolboxFilters(MetadataReader metadata, TypeDefinition definition)
		{
			var filters = new List<DesignerToolboxFilterInfo>();
			foreach (var handle in definition.GetCustomAttributes()) {
				var attribute = metadata.GetCustomAttribute(handle);
				if (AttributeTypeName(metadata, attribute.Constructor) != "System.ComponentModel.ToolboxItemFilterAttribute")
					continue;
				var bytes = metadata.GetBlobBytes(attribute.Value);
				if (bytes.Length < 3 || bytes[0] != 1 || bytes[1] != 0) continue;
				var offset = 2;
				var value = ReadSerializedString(bytes, ref offset);
				if (string.IsNullOrEmpty(value)) continue;
				var type = bytes.Length >= offset + 4 ? BitConverter.ToInt32(bytes, offset) switch {
					1 => "Custom", 2 => "Prevent", 3 => "Require", _ => "Allow"
				} : "Allow";
				filters.Add(new DesignerToolboxFilterInfo { FilterString = value, FilterType = type });
			}
			return filters;
		}

		static string? AttributeTypeName(MetadataReader metadata, EntityHandle constructor)
		{
			if (constructor.Kind == HandleKind.MethodDefinition) {
				var method = metadata.GetMethodDefinition((MethodDefinitionHandle)constructor);
				var type = metadata.GetTypeDefinition(method.GetDeclaringType());
				return FullName(metadata, type.Namespace, type.Name);
			}
			if (constructor.Kind != HandleKind.MemberReference) return null;
			var member = metadata.GetMemberReference((MemberReferenceHandle)constructor);
			if (member.Parent.Kind != HandleKind.TypeReference) return null;
			var referencedType = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
			return FullName(metadata, referencedType.Namespace, referencedType.Name);
		}

		static string? ReadSerializedString(byte[] bytes, ref int offset)
		{
			if (offset >= bytes.Length || bytes[offset] == 0xff) { offset++; return null; }
			var first = bytes[offset++];
			int length;
			if ((first & 0x80) == 0) length = first;
			else if ((first & 0xc0) == 0x80 && offset < bytes.Length) length = ((first & 0x3f) << 8) | bytes[offset++];
			else if ((first & 0xe0) == 0xc0 && offset + 2 < bytes.Length) length = ((first & 0x1f) << 24) | (bytes[offset++] << 16) | (bytes[offset++] << 8) | bytes[offset++];
			else return null;
			if (length < 0 || offset + length > bytes.Length) return null;
			var result = System.Text.Encoding.UTF8.GetString(bytes, offset, length);
			offset += length;
			return result;
		}

		static bool IsPublicTopLevel(TypeDefinition definition) =>
			(definition.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public;

		static AssemblyImage Open(string path, Dictionary<string, AssemblyImage> images)
		{
			var key = System.IO.Path.GetFullPath(path);
			if (images.TryGetValue(key, out var existing))
				return existing;
			var image = new AssemblyImage(key);
			images.Add(key, image);
			return image;
		}

		static bool IsUserControlBase(AssemblyImage image, EntityHandle type, IReadOnlyDictionary<string, string> paths,
			Dictionary<string, AssemblyImage> images, HashSet<string> visited)
			=> HasBase(image, type, paths, images, visited, "System.Windows.Controls.UserControl");

		static bool IsWpfVisualBase(AssemblyImage image, EntityHandle type, IReadOnlyDictionary<string, string> paths,
			Dictionary<string, AssemblyImage> images, HashSet<string> visited)
			=> HasBase(image, type, paths, images, visited, "System.Windows.FrameworkElement");

		static bool HasBase(AssemblyImage image, EntityHandle handle, IReadOnlyDictionary<string, string> paths,
			Dictionary<string, AssemblyImage> images, HashSet<string> visited, string wantedFullName)
		{
			if (handle.IsNil)
				return false;
			var resolved = Resolve(image, handle, paths, images);
			if (resolved == null)
				return false;
			var (owner, definition, fullName) = resolved.Value;
			if (fullName == wantedFullName)
				return true;
			var identity = owner.Path + "|" + fullName;
			return visited.Add(identity) && HasBase(owner, definition.BaseType, paths, images, visited, wantedFullName);
		}

		static (AssemblyImage Owner, TypeDefinition Definition, string FullName)? Resolve(AssemblyImage image, EntityHandle handle,
			IReadOnlyDictionary<string, string> paths, Dictionary<string, AssemblyImage> images)
		{
			if (handle.Kind == HandleKind.TypeDefinition) {
				var definition = image.Metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
				return (image, definition, FullName(image.Metadata, definition.Namespace, definition.Name));
			}
			if (handle.Kind != HandleKind.TypeReference)
				return null;
			var reference = image.Metadata.GetTypeReference((TypeReferenceHandle)handle);
			var scope = reference.ResolutionScope;
			if (scope.Kind != HandleKind.AssemblyReference)
				return null;
			var assemblyReference = image.Metadata.GetAssemblyReference((AssemblyReferenceHandle)scope);
			var assemblyName = image.Metadata.GetString(assemblyReference.Name);
			if (!paths.TryGetValue(assemblyName, out var path))
				return null;
			try {
				var owner = Open(path, images);
				var fullName = FullName(image.Metadata, reference.Namespace, reference.Name);
				foreach (var candidate in owner.Metadata.TypeDefinitions) {
					var definition = owner.Metadata.GetTypeDefinition(candidate);
					if (FullName(owner.Metadata, definition.Namespace, definition.Name) == fullName)
						return (owner, definition, fullName);
				}
			} catch (BadImageFormatException) {
				// Native references are irrelevant to a CLR inheritance chain.
			}
			return null;
		}

		static string FullName(MetadataReader metadata, StringHandle @namespace, StringHandle name)
		{
			var ns = metadata.GetString(@namespace);
			var typeName = metadata.GetString(name);
			return string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName;
		}
	}
}
