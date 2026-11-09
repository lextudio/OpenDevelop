// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using ICSharpCode.Core;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace ICSharpCode.PackageManagement
{
	/// <summary>A public type a package declares, and the package that ships it.</summary>
	public sealed class PackageType
	{
		public PackageType(string packageId, string version, string @namespace, string name)
		{
			PackageId = packageId;
			Version = version;
			Namespace = @namespace;
			Name = name;
		}

		public string PackageId { get; }
		public string Version { get; }
		public string Namespace { get; }
		public string Name { get; }
	}

	/// <summary>
	/// Which public types the packages a solution already uses declare, read from the compile assets of the
	/// packages in the global packages folder: it lets the light bulb offer "Install NuGet package" for a bare
	/// type name such as <c>JsonConvert</c>. Locating the files is NuGet's own code (the version folder layout,
	/// <see cref="PackageFolderReader"/>, nearest-framework selection); only the public type names are read here
	/// with System.Reflection.Metadata. Nothing is downloaded: a package that was never restored is not known.
	/// </summary>
	public static class PackageTypeIndex
	{
		// A dll's public types do not change while its path, size and time stay the same.
		static readonly ConcurrentDictionary<string, IReadOnlyList<(string Namespace, string Name)>> typesByAssembly =
			new ConcurrentDictionary<string, IReadOnlyList<(string Namespace, string Name)>>(StringComparer.Ordinal);

		/// <summary>
		/// The packages among <paramref name="packages"/> (id to version or version range) that declare a public
		/// type named <paramref name="typeName"/> (generic arity ignored, case sensitive), at most
		/// <paramref name="maxPackages"/>, in the order given.
		/// </summary>
		public static IReadOnlyList<PackageType> Find(
			string typeName, IReadOnlyDictionary<string, string> packages, string packagesFolder, NuGetFramework framework, int maxPackages = 3)
		{
			var found = new List<PackageType>();
			foreach (KeyValuePair<string, string> package in packages) {
				if (found.Count >= maxPackages) {
					break;
				}
				try {
					string? directory = FindPackageDirectory(packagesFolder, package.Key, package.Value);
					if (directory == null) {
						continue;
					}
					foreach (string assembly in CompileAssemblies(directory, framework)) {
						(string Namespace, string Name) match = TypesOf(assembly).FirstOrDefault(type => type.Name == typeName);
						if (match.Name != null) {
							found.Add(new PackageType(package.Key, package.Value, match.Namespace, match.Name));
							break;
						}
					}
				} catch (Exception ex) when (!(ex is OutOfMemoryException)) {
					LoggingService.Debug("Could not read the types of package '" + package.Key + "': " + ex.Message);
				}
			}
			return found;
		}

		/// <summary>The version folder of the best installed match for <paramref name="version"/> (a version or a range), or null.</summary>
		internal static string? FindPackageDirectory(string packagesFolder, string packageId, string version)
		{
			var resolver = new VersionFolderPathResolver(packagesFolder);
			if (NuGetVersion.TryParse(version, out NuGetVersion? exact)) {
				string path = resolver.GetInstallPath(packageId, exact);
				if (Directory.Exists(path)) {
					return path;
				}
			}
			if (!VersionRange.TryParse(version, out VersionRange? range)) {
				return null;
			}
			string idFolder = Path.Combine(packagesFolder, packageId.ToLowerInvariant());
			if (!Directory.Exists(idFolder)) {
				return null;
			}
			var installed = Directory.GetDirectories(idFolder)
				.Select(directory => NuGetVersion.TryParse(Path.GetFileName(directory), out NuGetVersion? parsed) ? parsed : null)
				.Where(parsed => parsed != null)
				.Select(parsed => parsed!)
				.ToList();
			NuGetVersion? best = range.FindBestMatch(installed);
			return best == null ? null : resolver.GetInstallPath(packageId, best);
		}

		/// <summary>The assemblies a project targeting <paramref name="framework"/> compiles against: the nearest <c>ref</c> group, else the nearest <c>lib</c> group.</summary>
		static IEnumerable<string> CompileAssemblies(string packageDirectory, NuGetFramework framework)
		{
			using var reader = new PackageFolderReader(packageDirectory);
			foreach (string folder in new[] { PackagingConstants.Folders.Ref, PackagingConstants.Folders.Lib }) {
				List<FrameworkSpecificGroup> groups = reader.GetItems(folder).ToList();
				FrameworkSpecificGroup? nearest = NuGetFrameworkUtility.GetNearest(groups, framework, group => group.TargetFramework);
				if (nearest != null) {
					return nearest.Items
						.Where(item => item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
						.Select(item => Path.Combine(packageDirectory, item.Replace('/', Path.DirectorySeparatorChar)))
						.ToList();
				}
			}
			return Array.Empty<string>();
		}

		/// <summary>The top-level public types of an assembly as (namespace, name without generic arity).</summary>
		public static IReadOnlyList<(string Namespace, string Name)> TypesOf(string assemblyPath)
		{
			var file = new FileInfo(assemblyPath);
			string key = assemblyPath + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
			return typesByAssembly.GetOrAdd(key, _ => ReadPublicTypes(assemblyPath));
		}

		static IReadOnlyList<(string Namespace, string Name)> ReadPublicTypes(string assemblyPath)
		{
			using FileStream stream = File.OpenRead(assemblyPath);
			using var peReader = new PEReader(stream);
			if (!peReader.HasMetadata) {
				return Array.Empty<(string, string)>();
			}
			MetadataReader metadata = peReader.GetMetadataReader();
			var types = new List<(string Namespace, string Name)>();
			foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions) {
				TypeDefinition type = metadata.GetTypeDefinition(handle);
				if (!type.GetDeclaringType().IsNil || (type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public) {
					continue;
				}
				string name = metadata.GetString(type.Name);
				int arity = name.IndexOf('`');
				types.Add((metadata.GetString(type.Namespace), arity < 0 ? name : name.Substring(0, arity)));
			}
			return types;
		}
	}
}
