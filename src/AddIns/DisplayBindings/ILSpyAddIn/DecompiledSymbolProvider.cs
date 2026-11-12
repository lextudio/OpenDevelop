using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;

namespace ICSharpCode.ILSpyAddIn
{
	/// <summary>
	/// Supplies the debugger with symbols for an assembly that has none: ILSpy decompiles the module, the decompiled C# files are
	/// written to a cache directory, and a portable PDB whose documents are those files lets the debugger stop, step and set
	/// breakpoints in them like in any source file. Registered at <see cref="IDecompiledSymbolProvider.AddInTreePath"/>, so
	/// disabling this AddIn switches decompiled debugging off. doc/technotes/decompiled-debugging.md.
	/// </summary>
	public sealed class DecompiledSymbolProvider : IDecompiledSymbolProvider
	{
		const string CompleteMarker = ".complete";

		// One generation per module directory at a time: a stop and a step can ask for the same module together. Keyed by the directory
		// (cache root + name + MVID), not the MVID alone, so providers with different cache roots never share a generation. Checked and
		// added under one lock, so two requests can never start two generations that write into the same directory.
		static readonly object inFlightLock = new object();
		static readonly Dictionary<string, Task<string>> inFlight = new Dictionary<string, Task<string>>();
		// A module that can never get symbols (no CodeView entry to take the PDB id from) is refused for the rest of the process: the adapter
		// asks again at every stop. Any other failure may be transient (disk full, a locked file, memory), so it is tried again, but only
		// MaxAttempts times, because a whole-module decompile is expensive and the adapter keeps asking. Restarting the IDE tries again.
		const int MaxAttempts = 3;
		static readonly HashSet<string> notGenerated = new HashSet<string>();
		static readonly Dictionary<string, int> failures = new Dictionary<string, int>();

		readonly string cacheRoot;

		/// <summary>The provider the AddIn tree builds: the cache lives in the user's config directory.</summary>
		public DecompiledSymbolProvider() : this(null)
		{
		}

		/// <param name="cacheRoot">The directory below which every module's PDB and decompiled sources live, or null for the default.</param>
		public DecompiledSymbolProvider(string cacheRoot)
		{
			this.cacheRoot = cacheRoot;
		}

		/// <summary>The directory below which every module's PDB and decompiled sources live: <c>&lt;name&gt;/&lt;mvid&gt;/</c>.</summary>
		public string CacheRoot {
			get { return cacheRoot ?? Path.Combine(SD.PropertyService.ConfigDirectory.ToString(), "cache", "DecompiledSymbols"); }
		}

		/// <remarks>
		/// The generation is shared by everyone who asks for the same module and is not cancelled by one of them: <paramref name="cancellationToken"/>
		/// only ends this caller's wait (an <see cref="OperationCanceledException"/>); the module is still generated, so the next request
		/// finds it in the cache.
		/// </remarks>
		public Task<string> GetSymbolsAsync(string assemblyPath, Guid mvid, IProgress<string> progress, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
				return Task.FromResult<string>(null);
			string moduleDirectory = GetModuleDirectory(CacheRoot, assemblyPath, mvid);
			Task<string> task;
			lock (inFlightLock) {
				if (notGenerated.Contains(moduleDirectory) || (failures.TryGetValue(moduleDirectory, out int failed) && failed >= MaxAttempts))
					return Task.FromResult<string>(null);
				if (!inFlight.TryGetValue(moduleDirectory, out task)) {
					string root = CacheRoot;
					task = Task.Run(() => Generate(root, assemblyPath, mvid, progress));
					inFlight[moduleDirectory] = task;
					task.ContinueWith(t => { lock (inFlightLock) inFlight.Remove(moduleDirectory); }, TaskScheduler.Default);
				}
			}
			return task.WaitAsync(cancellationToken);
		}

		/// <summary>The directory for one module: <c>&lt;root&gt;/&lt;assembly name&gt;/&lt;mvid&gt;</c>.</summary>
		public static string GetModuleDirectory(string cacheRoot, string assemblyPath, Guid mvid)
		{
			string name = Path.GetFileNameWithoutExtension(assemblyPath);
			return Path.Combine(cacheRoot, name, mvid.ToString("N"));
		}

		static string Generate(string cacheRoot, string assemblyPath, Guid mvid, IProgress<string> progress)
		{
			string name = Path.GetFileNameWithoutExtension(assemblyPath);
			string moduleDirectory = GetModuleDirectory(cacheRoot, assemblyPath, mvid);
			string pdbName = name + ".pdb";

			// A directory that is complete is never modified again, so it can be read without the publish lock
			if (IsComplete(moduleDirectory, pdbName))
				return Path.Combine(moduleDirectory, pdbName);

			// The cache is shared by every IDE instance of the user, and a directory that carries the marker is never modified again, so
			// each generation happens in a private directory next to it and is renamed into place at the end (atomic on one volume).
			// Another instance that finishes first wins; its result is used and this one is dropped.
			string tempDirectory = moduleDirectory + TempSuffix + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
			try {
				using (var file = new PEFile(assemblyPath, PEStreamOptions.PrefetchEntireImage)) {
					// The PDB must carry the id of the PE's CodeView entry; without one no debugger would accept it. Decided before anything is
					// decompiled or written, and remembered, because the adapter asks again at every stop in such a module.
					if (!DecompiledPdbWriter.HasCodeViewDebugDirectoryEntry(file)) {
						lock (inFlightLock) notGenerated.Add(moduleDirectory);
						progress?.Report(Path.GetFileName(assemblyPath) + " has no debug directory entry, so no symbols can be generated for it.");
						return null;
					}

					var watch = Stopwatch.StartNew();
					progress?.Report("Decompiling " + Path.GetFileName(assemblyPath) + " for debugging...");
					DeleteStaleTempDirectories(moduleDirectory);
					Directory.CreateDirectory(tempDirectory);

					var settings = new DecompilerSettings();
					var resolver = new UniversalAssemblyResolver(assemblyPath, false, file.DetectTargetFrameworkId());
					var typeSystem = new DecompilerTypeSystem(file, resolver, settings);
					// The documents are named by their FINAL paths, so the PDB is right once the directory is renamed into place
					var writer = new DecompiledPdbWriter { SourceRoot = Path.Combine(moduleDirectory, "src"), EmbedSourceFiles = false, NoLogo = true };
					Dictionary<string, string> sources;
					using (var stream = File.Create(Path.Combine(tempDirectory, pdbName))) {
						sources = writer.WritePdb(file, typeSystem, settings, stream);
					}
					// The PDB holds each document's SHA-256 of these exact bytes (UTF-8, no byte order mark)
					var encoding = new UTF8Encoding(false);
					foreach (var pair in sources) {
						string target = Path.Combine(tempDirectory, Path.GetRelativePath(moduleDirectory, pair.Key));
						Directory.CreateDirectory(Path.GetDirectoryName(target));
						File.WriteAllText(target, pair.Value, encoding);
						File.SetAttributes(target, FileAttributes.ReadOnly);
					}
					File.WriteAllText(Path.Combine(tempDirectory, CompleteMarker), string.Empty);

					Publish(tempDirectory, moduleDirectory, pdbName);
					progress?.Report("Decompiled " + Path.GetFileName(assemblyPath) + " in " + watch.Elapsed.TotalSeconds.ToString("F1") + " s.");
					return Path.Combine(moduleDirectory, pdbName);
				}
			} catch (Exception ex) {
				int attempts;
				lock (inFlightLock) {
					failures.TryGetValue(moduleDirectory, out attempts);
					failures[moduleDirectory] = ++attempts;
				}
				LoggingService.Warn("Decompiled debugging: could not generate symbols for " + assemblyPath, ex);
				progress?.Report("Could not decompile " + Path.GetFileName(assemblyPath) + " for debugging (attempt " + attempts + " of " + MaxAttempts + "): " + ex.Message);
				return null;
			} finally {
				// Gone already after a successful rename; otherwise whatever a failure left
				ResetDirectory(tempDirectory);
			}
		}

		const string TempSuffix = ".tmp-";

		/// <summary>The marker is written last, so a module directory that has it and its PDB is a finished generation.</summary>
		static bool IsComplete(string moduleDirectory, string pdbName)
		{
			return File.Exists(Path.Combine(moduleDirectory, CompleteMarker)) && File.Exists(Path.Combine(moduleDirectory, pdbName));
		}

		/// <summary>
		/// Moves the finished private directory to its place, or discards it when another instance got there first. The check, the removal of
		/// a leftover and the rename happen under an exclusive lock file shared by every instance of the user, so a directory another
		/// instance has just put in place can never be deleted or replaced here: a finished directory is never touched again. Only this short step is
		/// serialised; the decompiling is not.
		/// </summary>
		static void Publish(string tempDirectory, string moduleDirectory, string pdbName)
		{
			using (AcquirePublishLock(moduleDirectory + ".lock")) {
				if (IsComplete(moduleDirectory, pdbName))
					return;
				// What is here now is a leftover (a killed instance, or a marker without its PDB): nobody can be using it
				if (Directory.Exists(moduleDirectory))
					ResetDirectory(moduleDirectory);
				Directory.Move(tempDirectory, moduleDirectory);
			}
		}

		static FileStream AcquirePublishLock(string lockPath)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(lockPath));
			DateTime deadline = DateTime.UtcNow + PublishLockTimeout;
			while (true) {
				try {
					return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
				} catch (IOException) when (DateTime.UtcNow < deadline) {
					// Another instance is publishing a module, which takes moments
					Thread.Sleep(50);
				}
			}
		}

		static readonly TimeSpan PublishLockTimeout = TimeSpan.FromSeconds(30);

		/// <summary>Removes private directories of this module that a killed instance left behind; a recent one may belong to a live instance.</summary>
		static void DeleteStaleTempDirectories(string moduleDirectory)
		{
			string parent = Path.GetDirectoryName(moduleDirectory);
			if (!Directory.Exists(parent))
				return;
			foreach (var directory in Directory.EnumerateDirectories(parent, Path.GetFileName(moduleDirectory) + TempSuffix + "*")) {
				if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > StaleTempAge)
					ResetDirectory(directory);
			}
		}

		static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(6);

		static void ResetDirectory(string directory)
		{
			if (!Directory.Exists(directory))
				return;
			foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) {
				try { File.SetAttributes(path, FileAttributes.Normal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
			}
			try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
		}
	}
}
