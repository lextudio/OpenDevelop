// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Light-bulb action "Install NuGet package 'X'" for an unresolved namespace (CS0246/CS0234, BC30002/BC40056) in an
// SDK-style project. It suggests only packages that a project of the same solution already
// references (SolutionPackages), at the version the solution already uses: no network request is made
// while the caret settles, no version drifts between projects, and a package no project has used yet is
// added through Manage Packages instead. The qualified name at the diagnostic (for "using
// Newtonsoft.Json;", "Newtonsoft.Json") is matched to a known package id, then shorter prefixes
// ("Newtonsoft"). A bare type name such as JsonConvert cannot be mapped to a package this way.

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Refactoring;

namespace ICSharpCode.PackageManagement
{
	/// <summary>Adds a <see cref="MissingPackageContextActionProvider"/> to the editors of the languages that register this.</summary>
	public sealed class MissingPackageContextActionsExtension : ITextEditorExtension
	{
		ITextEditor? editor;
		MissingPackageContextActionProvider? provider;

		public void Attach(ITextEditor editor)
		{
			if (editor.ContextActionProviders.IsReadOnly)
				return;
			this.editor = editor;
			provider = new MissingPackageContextActionProvider();
			editor.ContextActionProviders.Add(provider);
		}

		public void Detach()
		{
			if (editor != null && provider != null)
				editor.ContextActionProviders.Remove(provider);
			editor = null;
			provider = null;
		}
	}

	public sealed class MissingPackageContextActionProvider : IContextActionProvider
	{
		// An unresolved type or namespace. C#: CS0246 (type or namespace not found) and CS0234 (not in the
		// namespace). Visual Basic: BC30002 (type not defined) and BC40056 (the namespace or type of an Imports
		// cannot be found); measured on an unreferenced "Imports X.Y" and "As X.Y.Z", where the span is the whole name.
		static readonly string[] unresolvedNameDiagnostics = { "CS0246", "CS0234", "BC30002", "BC40056" };

		// A bare name that is used as a type but cannot be resolved: besides CS0246/BC30002 (a type position),
		// CS0103/BC30451 (an expression such as JsonConvert.SerializeObject(...)). These are matched only
		// against the public types of packages the solution already uses (PackageTypeIndex), never as
		// a package id, so an unresolved variable name does not become a package suggestion.
		static readonly string[] unresolvedTypeNameDiagnostics = { "CS0246", "BC30002", "CS0103", "BC30451" };

		public string ID => "ICSharpCode.PackageManagement.MissingPackage";
		public string DisplayName => "Install missing NuGet package";
		public string Category => "NuGet";
		public bool AllowHiding => true;
		public bool IsVisible { get; set; } = true;

		public async Task<IContextAction[]> GetAvailableActionsAsync(EditorRefactoringContext context, CancellationToken cancellationToken)
		{
			try {
				return await GetActionsCoreAsync(context, cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) {
				// The renderer only catches cancellation from its async void timer handler.
				LoggingService.Warn("Missing-package suggestion failed for " + context.FileName + ": " + ex.Message);
				return Array.Empty<IContextAction>();
			}
		}

		async Task<IContextAction[]> GetActionsCoreAsync(EditorRefactoringContext context, CancellationToken cancellationToken)
		{
			var none = Array.Empty<IContextAction>();
			var fileName = context.FileName;
			if (fileName == null || !(SD.ProjectService.FindProjectContainingFile(fileName) is MSBuildBasedProject project)
				|| !project.IsSdkStyleProject || project.IsReadOnly)
				return none;

			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(fileName, out var service))
				return none;

			var id = new DocumentId(fileName.ToString());
			await service.UpsertDocumentAsync(id, context.TextSource.Text, cancellationToken).ConfigureAwait(false);
			var diagnostics = await service.GetDiagnosticsAsync(id, cancellationToken).ConfigureAwait(false);

			var text = context.TextSource.Text;
			var caret = context.CaretOffset;
			var caretLine = context.CaretLocation.Line;
			// Only a diagnostic on the caret's line can matter, so the offset conversion below (a scan
			// from the start of the text) is skipped for every other diagnostic in the file.
			foreach (var diagnostic in diagnostics.Where(d => (unresolvedNameDiagnostics.Contains(d.Id) || unresolvedTypeNameDiagnostics.Contains(d.Id))
				&& d.Span.Start.Line <= caretLine && caretLine <= d.Span.End.Line)) {
				int spanStart = GetOffset(text, diagnostic.Span.Start.Line, diagnostic.Span.Start.Column);
				// CS0246 spans the whole name, but CS0234 only the segment that does not exist
				// ("Json" in "using System.Text.Json;"), so take the dotted run around the span.
				var (runStart, qualified) = ReadQualifiedName(text, spanStart);
				// The caret has to be on the unresolved name (anywhere in the qualified name).
				if (qualified.Length == 0 || caret < runStart || caret > runStart + qualified.Length)
					continue;

				// Read the project model on the UI thread (it is not thread-safe; this method continues on a
				// pool thread): the packages the solution already uses and what this project already has.
				var (known, referenced) = await SD.MainThread.InvokeAsync(() => (
					SolutionPackages.Collect(SD.ProjectService.CurrentSolution.Projects.OfType<MSBuildBasedProject>()),
					SolutionPackages.ReferencedBy(project))).ConfigureAwait(false);
				if (unresolvedNameDiagnostics.Contains(diagnostic.Id)) {
					foreach (var candidate in Prefixes(qualified)) {
						if (referenced.Contains(candidate))
							return none; // already referenced here: the problem is a restore, not a missing reference
						if (known.TryGetValue(candidate, out string? version))
							return new IContextAction[] { new InstallMissingPackageAction(this, project, candidate, version) };
					}
				}

				// No package id matches: a bare type name may be declared by a package the solution already uses.
				if (unresolvedTypeNameDiagnostics.Contains(diagnostic.Id)
					&& diagnostic.Span.Start.Line == diagnostic.Span.End.Line
					&& IsSimpleIdentifier(text.Substring(spanStart, Math.Max(0, GetOffset(text, diagnostic.Span.End.Line, diagnostic.Span.End.Column) - spanStart)))) {
					string typeName = text.Substring(spanStart, GetOffset(text, diagnostic.Span.End.Line, diagnostic.Span.End.Column) - spanStart);
					var candidates = known.Where(pair => !referenced.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
					if (candidates.Count == 0)
						continue;
					// Where the packages are installed and what this project compiles against: project and settings model, UI thread.
					var (packagesFolder, framework) = await SD.MainThread.InvokeAsync(() => (GlobalPackagesFolder.ForOpenSolution(), TargetFramework(project))).ConfigureAwait(false);
					if (packagesFolder == null || framework == null)
						continue;
					var matches = await Task.Run(() => PackageTypeIndex.Find(typeName, candidates, packagesFolder, framework), cancellationToken).ConfigureAwait(false);
					if (matches.Count > 0)
						return matches.Select(match => (IContextAction)new InstallMissingPackageAction(this, project, match.PackageId, match.Version, match.Namespace + "." + match.Name)).ToArray();
				}
			}
			return none;
		}

		static bool IsSimpleIdentifier(string name) =>
			name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

		/// <summary>The framework the project compiles against (the first of TargetFrameworks for a multi-targeting project).</summary>
		static NuGet.Frameworks.NuGetFramework? TargetFramework(MSBuildBasedProject project)
		{
			string tfm = project.GetEvaluatedProperty("TargetFramework");
			if (string.IsNullOrWhiteSpace(tfm))
				tfm = (project.GetEvaluatedProperty("TargetFrameworks") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
			if (string.IsNullOrWhiteSpace(tfm))
				return null;
			var framework = NuGet.Frameworks.NuGetFramework.Parse(tfm.Trim());
			return framework.IsUnsupported ? null : framework;
		}

		/// <summary>Offset of a 1-based line/column in <paramref name="text"/>, clamped to its length.</summary>
		static int GetOffset(string text, int line, int column)
		{
			int offset = 0;
			for (int current = 1; offset < text.Length && current < line; )
				if (text[offset++] == '\n')
					current++;
			return Math.Min(text.Length, offset + Math.Max(0, column - 1));
		}

		/// <summary>
		/// The dotted identifier run that contains <paramref name="position"/>, without a trailing
		/// dot, and where it starts: it extends backwards over identifier characters and dots, then forwards.
		/// </summary>
		public static (int Start, string Name) ReadQualifiedName(string text, int position)
		{
			static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
			int start = Math.Min(position, text.Length);
			while (start > 0 && IsNamePart(text[start - 1]))
				start--;
			int end = Math.Min(position, text.Length);
			while (end < text.Length && IsNamePart(text[end]))
				end++;
			string run = text.Substring(start, end - start);
			int leadingDots = run.Length - run.TrimStart('.').Length;
			return (start + leadingDots, run.Trim('.'));
		}

		/// <summary>"A.B.C" -> "A.B.C", "A.B", "A": the package ids a qualified name can stand for, longest first.</summary>
		public static System.Collections.Generic.IEnumerable<string> Prefixes(string qualified)
		{
			var parts = qualified.Split('.', StringSplitOptions.RemoveEmptyEntries);
			for (int count = parts.Length; count >= 1; count--)
				yield return string.Join(".", parts.Take(count));
		}
	}

	sealed class InstallMissingPackageAction : IContextAction
	{
		readonly MSBuildBasedProject project;
		readonly string packageId;
		readonly string version;
		readonly string? declaredType;

		public InstallMissingPackageAction(IContextActionProvider provider, MSBuildBasedProject project, string packageId, string version, string? declaredType = null)
		{
			Provider = provider;
			this.project = project;
			this.packageId = packageId;
			this.version = version;
			this.declaredType = declaredType;
		}

		public IContextActionProvider Provider { get; }

		public string GetDisplayName(EditorRefactoringContext context)
			=> "Install NuGet package '" + packageId + "' " + version + " in '" + project.Name + "'"
				+ (declaredType == null ? "" : " (declares " + declaredType + ")");

		public void Execute(EditorRefactoringContext context)
		{
			_ = ExecuteAsync(context.FileName);
		}

		/// <summary>Public to the assembly so a test can await the install instead of polling.</summary>
		internal async Task<bool> ExecuteAsync(FileName documentFileName)
		{
			SD.StatusBar.SetMessage("Installing NuGet package '" + packageId + "' " + version + "...");
			try {
				var references = new SdkStylePackageReferenceService(project);
				// The project edit belongs to the UI thread (this method was started there); `dotnet restore` blocks, so it does not.
				references.AddPackageReference(packageId, version);
				string restoreFailure = await Task.Run(() => references.Restore(null)).ConfigureAwait(true);
				if (restoreFailure != null) {
					// The reference is already in the project; say that the restore did not work instead of
					// claiming the package was installed, and keep the reason for diagnosis.
					LoggingService.Warn("Restore after adding '" + packageId + "' failed: " + restoreFailure);
					SD.StatusBar.SetMessage("Added NuGet package '" + packageId + "' " + version + " to the project, but restoring it failed: " + restoreFailure, highlighted: true);
					return false;
				}
				// The PackageReference edit changes the project's metadata references. Reload the project
				// snapshot before announcing success, otherwise the language service keeps diagnosing the
				// newly-resolved namespace until the project happens to be reopened.
				var registry = SD.GetService<LanguageServiceRegistry>();
				if (registry != null) {
					try {
						await registry.RefreshProjectAsync(documentFileName.ToString(), CancellationToken.None).ConfigureAwait(true);
					} catch (Exception ex) {
						// The package is restored successfully even if refreshing editor assistance fails.
						// Retain the install result and make the delayed refresh diagnosable.
						LoggingService.Warn("Refreshing language service after installing '" + packageId + "' failed: " + ex.Message);
					}
				}
				SD.StatusBar.SetMessage("Installed NuGet package '" + packageId + "' " + version + ".");
				return true;
			} catch (Exception ex) {
				LoggingService.Warn("Installing '" + packageId + "' failed: " + ex);
				SD.StatusBar.SetMessage("Installing NuGet package '" + packageId + "' failed: " + ex.Message, highlighted: true);
				return false;
			}
		}
	}
}
