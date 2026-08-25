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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.WpfDesign.Designer.PropertyGrid;
using ICSharpCode.WpfDesign.AddIn.OutOfProcess;
using ICSharpCode.WpfDesign.AddIn.Options;
using ICSharpCode.WpfDesign.SurfaceHost;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// IViewContent implementation that hosts the WPF designer.
	///
	/// Out-of-process cutover (doc/technotes/wpf-designer.md): drives a spawned
	/// <see cref="WpfSurfaceHostClient"/>/<see cref="WpfSurfaceDesignerControl"/> instead of a
	/// live in-process <c>DesignSurface</c>, mirroring FormsDesignerViewContent/
	/// WinUIXamlDesignerViewContent's own already-converged shape. The host never touches a
	/// target-defined type or loads a project assembly - all real WPF objects, and all type
	/// resolution, live in the child (matches designer-common.md's "no target-type knowledge in
	/// the host" red line the in-process version could not honor).
	/// </summary>
	public class WpfViewContent : AbstractViewContentHandlingLoadErrors, IToolsHost, IOutlineContentHost, IHasPropertyContainer
	{
		public WpfViewContent(OpenedFile file) : base(file)
		{
			commands.RegisterStandard(() => undoStack.Count > 0 && client != null && surfaceControl != null, UndoCore,
				() => redoStack.Count > 0 && client != null && surfaceControl != null, RedoCore);
			this.TabPageText = "${res:FormsDesigner.DesignTabPages.DesignTabPage}";
			this.IsActiveViewContentChanged += OnIsActiveViewContentChanged;
			Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
		}

		WpfSurfaceHostClient? client;
		WpfSurfaceDesignerControl? surfaceControl;
		long documentVersion;
		bool hasLoadedOnce;
		bool wasChangedInDesigner;
		MemoryStream? _stream;

		// Undo/redo: whole-document XAML text snapshot stacks, mirroring
		// DesignerViewContent's own remoteUndo/remoteRedo pattern for the WinForms designer -
		// neither backend does real live-element-tree transactional undo (no such RPC exists on
		// either child host); both instead snapshot/restore the WHOLE flushed document text
		// through the existing session/update RPC (client.UpdateAsync), which is already used for
		// every document reload. lastKnownGoodXaml is the text as of the last accepted mutation
		// (or load); OnDocumentChanged pushes it onto undoStack right before replacing it with the
		// new post-mutation text, so Undo can restore exactly what preceded that mutation.
		readonly Stack<string> undoStack = new();
			readonly Stack<string> redoStack = new();
			readonly DesignerCommandController commands = new();
		string? lastKnownGoodXaml;

			public bool CanUndo => commands.CanExecute("Undo");
			public bool CanRedo => commands.CanExecute("Redo");

		/// <summary>The current out-of-process surface, or null before the first successful load.
		/// Exposed for DevFlow probes (<c>WpfDesignDevFlowActions</c>) - real UI code should go
		/// through this rather than reaching into <see cref="client"/> directly.</summary>
		public WpfSurfaceDesignerControl? SurfaceControl => surfaceControl;

		/// <summary>Surface geometry for the resize-drag smoke test: the rendered design bitmap
		/// bounds (frame), the selected element's rendered bounds (element) and its selection
		/// outline (selection) in screen coordinates, plus the bottom-right resize handle. The
		/// selection outline hugs the element exactly (the tree-bounds/rendered-pixel
		/// coordinate mismatch is fixed - see wpf-designer.md), and all three are the shared
		/// <see cref="DesignerSurfaceGeometry"/> shape every designer reports.
		/// </summary>
		public DesignerSurfaceGeometry SurfaceGeometry()
			=> surfaceControl?.SurfaceGeometry() ?? default;

		protected override void LoadInternal(OpenedFile file, Stream stream)
		{
			Debug.Assert(file == this.PrimaryFile);
			SD.AnalyticsMonitor.TrackFeature(typeof(WpfViewContent), "Load");

			_stream = new MemoryStream();
			stream.CopyTo(_stream);
			stream.Position = 0;
			wasChangedInDesigner = false;
			undoStack.Clear();
			redoStack.Clear();
			lastKnownGoodXaml = null;

			if (surfaceControl == null)
			{
				LoggingService.Info("WPF designer: acquiring shared surface host");
				client = WpfSurfaceHostClient.AcquireSharedAsync(null, CancellationToken.None).GetAwaiter().GetResult();
				LoggingService.Info($"WPF designer: acquired surface host pid={client.ProcessId}");
				surfaceControl = new WpfSurfaceDesignerControl(client);
				surfaceControl.SelectionChanged += OnSelectionChanged;
				surfaceControl.DocumentChanged += OnDocumentChanged;
				surfaceControl.UndoRedoRequested += OnUndoRedoRequested;
				InitPropertyEditor();
				InitWpfToolbox();
			}
			this.UserContent = surfaceControl;

			try
			{
				var snapshot = CreateSnapshot(++documentVersion);
				LoggingService.Info($"WPF designer: opening document version={snapshot.Version}");
				var state = hasLoadedOnce
					? surfaceControl.UpdateAsync(snapshot).GetAwaiter().GetResult()
					: surfaceControl.OpenAsync(snapshot).GetAwaiter().GetResult();
				LoggingService.Info($"WPF designer: document response accepted={state.Accepted}");
				// OpenAsync/UpdateAsync deliberately do not render internally (see
				// WpfSurfaceDesignerControl.Show's remarks) - GetResult() above resumed execution
				// on this thread, which IS the dispatcher thread here (LoadInternal always runs on
				// it), so calling Show() directly, synchronously, right here is correct and safe.
				surfaceControl.Show(state);
				if (!state.Accepted)
					throw new WpfDesignerLoadException(state.Error);
				hasLoadedOnce = true;
				UpdateTasks(state.Diagnostics);
				UpdateOutline(state);
				propertyContainer.SelectedObject = null;
				// Baseline for Undo/Redo: the first mutation's OnDocumentChanged pushes THIS text
				// (not the not-yet-fetched post-mutation text) onto undoStack.
				LoggingService.Info("WPF designer: flushing initial document baseline");
				lastKnownGoodXaml = FlushCurrentXaml();
				LoggingService.Info("WPF designer: initial document baseline ready");
			}
			catch (Exception e)
			{
				ShowDesignerError(e);
			}
		}

		DesignerDocumentSnapshot CreateSnapshot(long version)
		{
			var project = SD.ProjectService.FindProjectContainingFile(PrimaryFile.FileName);
			var snapshot = new DesignerDocumentSnapshot {
				Version = version,
				ProjectFileName = project?.FileName.ToString() ?? "",
				TargetFramework = (project as MSBuildBasedProject)?.GetEvaluatedProperty("TargetFramework") ?? "",
				ProjectAssemblyPath = GetManagedAssemblyPath(project),
				PrimaryFileName = PrimaryFile.FileName.ToString(),
				Language = ""
			};
			// Do not run MSBuild ResolveAssemblyReferences synchronously from LoadInternal. It can
			// block the dispatcher indefinitely while project evaluation/build hosts are busy, which
			// also makes every DevFlow request time out. The child resolves dependencies beside the
			// project output; explicit reference paths are reserved for already-available metadata,
			// never computed on the UI thread.

			_stream!.Position = 0;
			using (var reader = new StreamReader(new UnclosableStream(_stream)))
			{
				snapshot.Files.Add(new DesignerSourceFileSnapshot {
					FileName = snapshot.PrimaryFileName, Kind = "Source", Text = reader.ReadToEnd()
				});
			}

			if (WpfEditorOptions.EnableAppXamlParsing && project != null)
			{
				var appXamlItem = project.Items.OfType<FileProjectItem>()
					.FirstOrDefault(item => item.FileName.GetFileName().Equals("app.xaml", StringComparison.OrdinalIgnoreCase));
				if (appXamlItem != null)
				{
					try
					{
						var appFile = SD.FileService.GetOrCreateOpenedFile(appXamlItem.FileName);
						using var appStream = appFile.OpenRead();
						using var appReader = new StreamReader(appStream);
						snapshot.Files.Add(new DesignerSourceFileSnapshot {
							FileName = appXamlItem.FileName.ToString(), Kind = "AppXaml", Text = appReader.ReadToEnd()
						});
					}
					catch (Exception ex)
					{
						LoggingService.Warn("WPF designer: could not read app.xaml for resource loading", ex);
					}
				}
			}
			return snapshot;
		}

		/// <summary>OutputAssemblyFullPath can point at the apphost (an extensionless executable
		/// on Unix, or a ".exe" native shim on Windows) instead of the managed assembly; the child
		/// needs the managed ".dll", so prefer the sibling when it exists - same fix
		/// FormsDesignerViewContent.GetManagedAssemblyPath already established.</summary>
		static string GetManagedAssemblyPath(IProject? project)
		{
			var path = project?.OutputAssemblyFullPath.ToString() ?? "";
			if (string.IsNullOrEmpty(path) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				return path;
			var dll = Path.ChangeExtension(path, ".dll");
			return File.Exists(dll) ? dll : path;
		}

		void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
		{
			if (IsDisposed || !IsActiveViewContent || surfaceControl == null || UserContent != surfaceControl)
				return;

			LoggingService.Error("Unhandled WPF designer UI exception", e.Exception);
			e.Handled = true;
			ShowDesignerError(e.Exception);
		}

		void ShowDesignerError(Exception exception)
		{
			outline.SetRoot(null!);
			propertyContainer.SelectedObject = null;
			this.UserContent = new WpfDocumentError(exception);
		}

		protected override void SaveInternal(OpenedFile file, Stream stream)
		{
			if (wasChangedInDesigner && client != null)
			{
				SD.AnalyticsMonitor.TrackFeature(typeof(WpfViewContent), "Save");
				var edit = client.FlushAsync(documentVersion).GetAwaiter().GetResult();
				var text = edit.Files.FirstOrDefault(f => f.FileName == PrimaryFile.FileName.ToString())?.Text
					?? edit.Files.FirstOrDefault()?.Text ?? "";
				using var writer = new StreamWriter(stream, leaveOpen: true);
				writer.Write(text);
			}
			else
			{
				_stream!.Position = 0;
				using var reader = new StreamReader(new UnclosableStream(_stream));
				using var writer = new StreamWriter(stream, leaveOpen: true);
				writer.Write(reader.ReadToEnd());
			}
		}

		public static List<SDTask> DllLoadErrors = new List<SDTask>();
		List<SDTask> tasks = new List<SDTask>();

		void UpdateTasks(List<DesignerDiagnostic> diagnostics)
		{
			foreach (var task in tasks)
				TaskService.Remove(task);
			tasks.Clear();

			foreach (var diagnostic in diagnostics)
			{
				var task = new SDTask(PrimaryFile.FileName, diagnostic.Message, Math.Max(0, diagnostic.Column - 1), diagnostic.Line, SharpDevelop.TaskType.Error);
				tasks.Add(task);
				TaskService.Add(task);
			}

			TaskService.AddRange(DllLoadErrors);

			if (diagnostics.Count != 0)
				SD.Workbench.GetPad("ICSharpCode.SharpDevelop.Gui.ErrorListPad").BringPadToFront();
		}

		void OnSelectionChanged(object? sender, EventArgs e)
		{
			var adapters = surfaceControl?.SelectedPropertyAdapters ?? Array.Empty<object>();
			propertyContainer.SelectedObject = adapters.Length > 1 ? new DesignerMultiPropertyAdapter(adapters) : adapters.FirstOrDefault();
			shellSelection.Select(surfaceControl?.SelectedElementIds ?? Array.Empty<string>());
			// Design surface -> Document Outline: mirror the selection without re-triggering the
			// outline -> surface path. The outline raises SelectionCommitted for a programmatic
			// selection too, so an explicit guard is required to preserve secondary selections.
			syncingOutlineSelection = true;
			try { outline.SelectNodeById(surfaceControl?.SelectedElementId); }
			finally { syncingOutlineSelection = false; }
			CommandManager.InvalidateRequerySuggested();
		}

		/// <summary>Marks the file dirty after any accepted mutation (bounds/add/delete/rename/
		/// property edit) - <see cref="WpfSurfaceDesignerControl.DocumentChanged"/>'s only
		/// subscriber. NOT wired off <see cref="DesignerSessionState.Version"/> comparisons: the
		/// DDP's <c>state.Version</c> is never actually bumped by a mutation RPC on this backend
		/// (see the event's own doc comment on <c>WpfSurfaceDesignerControl</c>), so a version-diff
		/// check silently never fires - this was a real bug, caught by
		/// <c>SelectControl_EditingContentInPropertiesPad_UpdatesAndSavesXaml</c> asserting
		/// <c>od.file.is-dirty</c> after a Properties-pad edit.</summary>
		void OnDocumentChanged(object? sender, DesignerSessionState state)
		{
			Console.Error.WriteLine("DIAG5 OnDocumentChanged accepted=" + state.Accepted + " file=" + PrimaryFile.FileName + " isDirtyBefore=" + PrimaryFile.IsDirty);
			if (!state.Accepted)
				return;
			// Undo/redo bookkeeping: the text as of just before this mutation becomes the entry
			// Undo restores; a fresh mutation always invalidates the redo stack. Skipped when
			// lastKnownGoodXaml is itself null, which only happens if a mutation somehow lands
			// before LoadInternal's own post-load flush completes.
			if (lastKnownGoodXaml != null)
			{
				undoStack.Push(lastKnownGoodXaml);
				redoStack.Clear();
			}
			lastKnownGoodXaml = FlushCurrentXaml();
			wasChangedInDesigner = true;
			this.PrimaryFile.MakeDirty();
			Console.Error.WriteLine("DIAG5 after MakeDirty isDirty=" + PrimaryFile.IsDirty);
		}

		string FlushCurrentXaml()
		{
			var edit = client!.FlushAsync(documentVersion).GetAwaiter().GetResult();
			return edit.Files.FirstOrDefault(f => f.FileName == PrimaryFile.FileName.ToString())?.Text
				?? edit.Files.FirstOrDefault()?.Text ?? "";
		}

		/// <summary>Restores a previously-flushed whole-document XAML text via
		/// <c>session/update</c> (the same RPC every document reload already uses) - see the
		/// undoStack/redoStack fields' own doc comment for why this is the right level for WPF's
		/// undo, matching what WinForms already does.</summary>
		void OnUndoRedoRequested(object? sender, bool undo)
		{
			if (undo)
				Undo();
			else
				Redo();
		}

			public void Undo() => commands.Execute("Undo");
			bool UndoCore()
			{
				redoStack.Push(lastKnownGoodXaml ?? FlushCurrentXaml());
				RestoreXaml(undoStack.Pop());
				return true;
			}

			public void Redo() => commands.Execute("Redo");
			bool RedoCore()
			{
				undoStack.Push(lastKnownGoodXaml ?? FlushCurrentXaml());
				RestoreXaml(redoStack.Pop());
				return true;
			}

		void RestoreXaml(string text)
		{
			var snapshot = CreateSnapshot(++documentVersion);
			var primary = snapshot.Files.FirstOrDefault(f => f.FileName == snapshot.PrimaryFileName);
			if (primary != null)
				primary.Text = text;
			var state = surfaceControl!.UpdateAsync(snapshot).GetAwaiter().GetResult();
			surfaceControl.Show(state);
			if (!state.Accepted)
				throw new WpfDesignerLoadException(state.Error);
			lastKnownGoodXaml = text;
			wasChangedInDesigner = true;
			UpdateTasks(state.Diagnostics);
			UpdateOutline(state);
			PrimaryFile.MakeDirty();
		}

		#region Property editor / Outline

		PropertyGridView? propertyGridView;
		readonly PropertyContainer propertyContainer = new PropertyContainer();

		public PropertyContainer PropertyContainer => propertyContainer;

		void InitPropertyEditor()
		{
			propertyGridView = new PropertyGridView();
			propertyGridView.PropertyGrid.PropertyChanged += OnPropertyGridPropertyChanged;
		}

		void InitWpfToolbox()
		{
			// Never resolve or load project references on the workbench dispatcher. Besides freezing
			// the whole IDE while MSBuild runs, AddProjectDlls loaded untrusted project assemblies
			// into the IDE process and defeated the out-of-process designer boundary. Stock controls
			// are already present; project controls are supplied as neutral toolbox DTOs by the child.
			SD.ProjectService.ProjectItemAdded += OnReferenceAdded;
		}

		void OnReferenceAdded(object sender, ProjectItemEventArgs e)
		{
			if (!(e.ProjectItem is ReferenceProjectItem)) return;
			if (e.Project != SD.ProjectService.FindProjectContainingFile(Files[0].FileName)) return;
			// The next child state refresh republishes the runtime-derived toolbox catalogue.
		}

		void OnPropertyGridPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (propertyGridView == null || propertyGridView.PropertyGrid.ReloadActive) return;
			if (e.PropertyName != "Name") return;
			if (!propertyGridView.PropertyGrid.IsNameCorrect) return;

			OpenedFile? file = this.Files.FirstOrDefault(f => f.FileName.ToString().EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
			if (file == null) return;

			string oldName = propertyGridView.PropertyGrid.OldName;
			string newName = propertyGridView.PropertyGrid.Name;
			WpfControlRenameSync.RenameAsync(file.FileName, oldName, newName).FireAndForget();
		}

		#endregion

		public object ToolsContent => WpfToolbox.Instance.ToolboxControl;

		public override void Dispose()
		{
			Application.Current.DispatcherUnhandledException -= OnDispatcherUnhandledException;
			SD.ProjectService.ProjectItemAdded -= OnReferenceAdded;
			if (surfaceControl != null)
			{
				surfaceControl.SelectionChanged -= OnSelectionChanged;
				surfaceControl.DocumentChanged -= OnDocumentChanged;
				surfaceControl.UndoRedoRequested -= OnUndoRedoRequested;
			}
			outline.SelectionCommitted -= OnOutlineSelectionCommitted;
			client?.Dispose();
			client = null;

			base.Dispose();
		}

		void OnIsActiveViewContentChanged(object sender, EventArgs e)
		{
			if (IsActiveViewContent && surfaceControl != null)
			{
				WpfToolbox.Instance.ToolService = null;
			}
		}

		readonly DocumentOutlineControl outline = new DocumentOutlineControl();
		readonly DesignerSelectionController shellSelection = new DesignerSelectionController();

		void UpdateOutline(DesignerSessionState state)
		{
			shellSelection.UpdateTree(state.Tree);
			outline.SetRoots(shellSelection.Roots);
			if (!outlineSubscribed)
			{
				outline.SelectionCommitted += OnOutlineSelectionCommitted;
				outlineSubscribed = true;
			}
		}

		bool outlineSubscribed;
		bool syncingOutlineSelection;

		void OnOutlineSelectionCommitted(object sender, EventArgs e)
		{
			if (syncingOutlineSelection)
				return;
			// Outline -> design surface: the surface owns selection; route the pick through the
			// same single-selection path as a surface click.
			shellSelection.Select(outline.SelectedNode?.Id);
			surfaceControl?.SelectElementId(shellSelection.PrimarySelectedId);
		}

		/// <summary>The DESIGNER's own element tree, matching what
		/// <c>FormsDesignerViewContent</c> and <c>WinUIXamlDesignerViewContent</c> both return
		/// (each simply hands back its own outline). This used to walk the sibling views and
		/// return the SOURCE editor's outline instead - a leftover from the old in-process
		/// designer, which had no outline of its own - so with the Design tab active the Outline
		/// pad showed the XAML text editor's LSP symbol list rather than the designed element
		/// tree, and the tree built in <see cref="UpdateOutline"/> was never displayed at all.
		/// The source view keeps its own <c>IOutlineContentHost</c> (XamlOutlineContentHost), so
		/// switching to the Source tab still shows the source outline - that split is the point.</summary>
		public object OutlineContent => outline;
	}

	/// <summary>Thrown when the child rejects <c>session/open</c>/<c>session/update</c> (e.g. a
	/// XAML parse error) - caught by <see cref="WpfViewContent.LoadInternal"/> the same way the
	/// old in-process load's catch-all did, showing <see cref="WpfDocumentError"/> instead of
	/// leaving the view half-loaded.</summary>
	[Serializable]
	public class WpfDesignerLoadException : Exception
	{
		public WpfDesignerLoadException(string message) : base(message) { }
	}
}
