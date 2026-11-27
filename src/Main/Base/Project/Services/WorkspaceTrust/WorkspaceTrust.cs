using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>Whether the open workspace may execute code that came from its own folder.</summary>
	public enum TrustState
	{
		Trusted,
		Restricted
	}

	/// <summary>An operation that can execute workspace-supplied code. See doc/technotes/workspace-trust.md.</summary>
	[Flags]
	public enum TrustCapability
	{
		None = 0,
		/// <summary>Third-party Roslyn analyzers and source generators, loaded into the IDE process.</summary>
		LoadAnalyzers = 1,
		/// <summary>Anything that runs MSBuild targets: restore, build, clean, reference resolution, slnx migration.</summary>
		ExecuteBuild = 2,
		/// <summary>Run, debug or test the project's output.</summary>
		RunProgram = 4,
		/// <summary>Designers (the Design tab and other secondary views), which load project assemblies and run control constructors.</summary>
		LoadProjectAssemblies = 8,
		/// <summary>A shell in the workspace folder.</summary>
		LaunchTerminal = 16,
		/// <summary>Language services: completion, diagnostics, navigation, semantic colouring. Needs references, so it also starts language hosts.</summary>
		SemanticAnalysis = 32,
	}

	public sealed class WorkspaceNotTrustedException : InvalidOperationException
	{
		public TrustCapability Capability { get; }

		public WorkspaceNotTrustedException(TrustCapability capability)
			: base(WorkspaceTrust.DescribeDenial(capability))
		{
			Capability = capability;
		}
	}

	public interface IWorkspaceTrustService
	{
		/// <summary>False when trust is switched off: every capability is then allowed.</summary>
		bool IsEnabled { get; }

		/// <summary>The trust root of the open workspace, or null when none is open.</summary>
		string CurrentWorkspaceRoot { get; }

		TrustState CurrentState { get; }

		IReadOnlyList<string> TrustedRoots { get; }

		/// <summary>Raised after the current workspace or the trusted list changed the current state.</summary>
		event EventHandler TrustChanged;

		TrustState GetState(string path);

		/// <summary>The folder a trust decision for a solution or project file applies to.</summary>
		string GetTrustRoot(string solutionOrProjectFile);

		/// <summary>False for a filesystem root or the user profile folder: trusting those covers far too much.</summary>
		bool CanTrustRoot(string path);

		void SetCurrentWorkspace(string root);

		void Trust(string root, string source = "user");

		void Revoke(string root);

		bool IsAllowed(TrustCapability capability);
	}

	public static class WorkspaceTrust
	{
		public static bool IsAllowed(TrustCapability capability)
		{
			return SD.GetService<IWorkspaceTrustService>()?.IsAllowed(capability) ?? true;
		}

		/// <summary>Returns whether the operation may run; when not, tells the user why in the status bar.</summary>
		public static bool Demand(TrustCapability capability)
		{
			if (IsAllowed(capability))
				return true;
			string message = DescribeDenial(capability);
			LoggingService.Info(message);
			try {
				SD.StatusBar?.SetMessage(message);
			} catch (Exception) {
				// No workbench yet (start-up, tools): the log line above is enough.
			}
			return false;
		}

		public static void ThrowIfDenied(TrustCapability capability)
		{
			if (!IsAllowed(capability))
				throw new WorkspaceNotTrustedException(capability);
		}

		/// <summary>
		/// A UI string from the resource files (StringResources*.resx), or <paramref name="english"/> when the resource
		/// service is not running (unit tests, tools) or has no such key.
		/// </summary>
		public static string Text(string key, string english)
		{
			try {
				return ResourceService.GetString(key) ?? english;
			} catch (Exception) {
				return english;
			}
		}

		public static string DescribeDenial(TrustCapability capability)
		{
			var (name, what) = capability switch {
				TrustCapability.LoadAnalyzers => ("LoadAnalyzers", "Loading analyzers and source generators"),
				TrustCapability.ExecuteBuild => ("ExecuteBuild", "Restoring and building"),
				TrustCapability.RunProgram => ("RunProgram", "Running, debugging and testing"),
				TrustCapability.LoadProjectAssemblies => ("LoadProjectAssemblies", "Loading project assemblies in a designer"),
				TrustCapability.LaunchTerminal => ("LaunchTerminal", "Starting a terminal"),
				TrustCapability.SemanticAnalysis => ("SemanticAnalysis", "Semantic analysis"),
				_ => ("Default", "This operation")
			};
			return Text("WorkspaceTrust.Denied." + name,
				what + " is disabled because this workspace is in Restricted Mode. Use Tools > Workspace Trust to trust it.");
		}

		/// <summary>Part of the language-service cache fingerprint: a restricted snapshot has no resolved references.</summary>
		public static string FingerprintToken()
		{
			var service = SD.GetService<IWorkspaceTrustService>();
			return service != null && service.IsEnabled && service.CurrentState == TrustState.Restricted ? "restricted" : "trusted";
		}
	}

	public sealed class WorkspaceTrustService : IWorkspaceTrustService
	{
		sealed class Entry
		{
			public string Path { get; set; }
			public DateTime TrustedAt { get; set; }
			public string Source { get; set; }
		}

		sealed class StoreFile
		{
			public int Version { get; set; } = 1;
			public List<Entry> Trusted { get; set; } = new List<Entry>();
		}

		static readonly StringComparison PathComparison =
			OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

		readonly object gate = new object();
		readonly string storePath;
		List<Entry> entries;
		string currentRoot;

		public WorkspaceTrustService() : this(DefaultStorePath(), DefaultEnabled())
		{
		}

		public WorkspaceTrustService(string storePath, bool enabled)
		{
			this.storePath = storePath;
			IsEnabled = enabled;
		}

		public bool IsEnabled { get; }

		public event EventHandler TrustChanged;

		static string DefaultStorePath()
		{
			string directory = null;
			try {
				directory = SD.PropertyService?.ConfigDirectory?.ToString();
			} catch (Exception) {
			}
			if (string.IsNullOrEmpty(directory))
				directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenDevelop");
			return Path.Combine(directory, "workspace-trust.json");
		}

		public static bool DefaultEnabled()
		{
			switch (Environment.GetEnvironmentVariable("OD_WORKSPACE_TRUST")) {
				case "0":
					return false;
				case "1":
					return true;
				default:
					return !TestMode.IsActive;
			}
		}

		public string CurrentWorkspaceRoot {
			get { lock (gate) return currentRoot; }
		}

		public TrustState CurrentState {
			get {
				string root = CurrentWorkspaceRoot;
				return root == null ? TrustState.Trusted : GetState(root);
			}
		}

		public IReadOnlyList<string> TrustedRoots {
			get {
				lock (gate)
					return Entries().Select(e => e.Path).ToList();
			}
		}

		public TrustState GetState(string path)
		{
			if (!IsEnabled)
				return TrustState.Trusted;
			if (string.IsNullOrEmpty(path))
				return TrustState.Restricted;
			string full = Normalize(path);
			lock (gate)
				return Entries().Any(e => IsUnder(full, e.Path)) ? TrustState.Trusted : TrustState.Restricted;
		}

		public string GetTrustRoot(string solutionOrProjectFile)
		{
			string directory = Normalize(Path.GetDirectoryName(Path.GetFullPath(solutionOrProjectFile)));
			for (string probe = directory; probe != null; probe = Path.GetDirectoryName(probe)) {
				string git = Path.Combine(probe, ".git");
				if (Directory.Exists(git) || File.Exists(git))
					return CanTrustRoot(probe) ? Normalize(probe) : directory;
			}
			return directory;
		}

		public bool CanTrustRoot(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			string full = Normalize(path);
			if (Path.GetPathRoot(full) is string root && string.Equals(Normalize(root), full, PathComparison))
				return false;
			string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return string.IsNullOrEmpty(profile) || !string.Equals(Normalize(profile), full, PathComparison);
		}

		public void SetCurrentWorkspace(string root)
		{
			TrustState before = CurrentState;
			lock (gate)
				currentRoot = string.IsNullOrEmpty(root) ? null : Normalize(root);
			if (CurrentState != before || root != null)
				RaiseTrustChanged();
		}

		public void Trust(string root, string source = "user")
		{
			if (!CanTrustRoot(root))
				throw new ArgumentException("This folder is too broad to trust: " + root, nameof(root));
			string full = Normalize(root);
			lock (gate) {
				var list = Entries();
				if (list.Any(e => string.Equals(e.Path, full, PathComparison)))
					return;
				list.Add(new Entry { Path = full, TrustedAt = DateTime.UtcNow, Source = source });
				Save();
			}
			RaiseTrustChanged();
		}

		public void Revoke(string root)
		{
			string full = Normalize(root);
			lock (gate) {
				if (Entries().RemoveAll(e => string.Equals(e.Path, full, PathComparison)) == 0)
					return;
				Save();
			}
			RaiseTrustChanged();
		}

		public bool IsAllowed(TrustCapability capability)
		{
			return !IsEnabled || CurrentState == TrustState.Trusted;
		}

		void RaiseTrustChanged()
		{
			TrustChanged?.Invoke(this, EventArgs.Empty);
		}

		List<Entry> Entries()
		{
			if (entries != null)
				return entries;
			entries = new List<Entry>();
			try {
				if (File.Exists(storePath)) {
					var store = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(storePath), JsonOptions);
					if (store?.Trusted != null)
						entries.AddRange(store.Trusted.Where(e => !string.IsNullOrWhiteSpace(e.Path)));
				}
			} catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException) {
				// An unreadable store means nothing is trusted; the file is left alone until the next
				// explicit decision rewrites it.
				LoggingService.Warn("Could not read " + storePath + "; no folder is trusted until it is fixed.", ex);
			}
			return entries;
		}

		static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

		void Save()
		{
			string directory = Path.GetDirectoryName(storePath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);
			string temp = storePath + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(new StoreFile { Trusted = entries }, JsonOptions));
			File.Move(temp, storePath, overwrite: true);
		}

		static string Normalize(string path)
		{
			string full = Path.GetFullPath(path);
			string root = Path.GetPathRoot(full) ?? string.Empty;
			if (full.Length > root.Length)
				full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return full;
		}

		static bool IsUnder(string path, string root)
		{
			if (string.Equals(path, root, PathComparison))
				return true;
			string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
			return path.StartsWith(prefix, PathComparison);
		}
	}
}
