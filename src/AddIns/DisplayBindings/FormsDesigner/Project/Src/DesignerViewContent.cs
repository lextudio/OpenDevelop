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
using System.ComponentModel.Design;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.WinForms;
using ICSharpCode.SharpDevelop.Workbench;
using Xceed.Wpf.Toolkit.PropertyGrid;
using System.Windows;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.FormsDesigner.Services;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.FormsDesigner.OutOfProcess;

namespace ICSharpCode.FormsDesigner
{
	public class FormsDesignerViewContent : AbstractViewContentHandlingLoadErrors, IClipboardHandler, IUndoHandler, IHasPropertyContainer, IContextHelpProvider, IToolsHost, IFileDocumentProvider, IOutlineContentHost
	{
		// The SideBar-backed drag-from-toolbox panel (ToolboxProvider.FormsDesignerSideBar) stays
		// unused - Services.ToolboxService (the real System.Drawing.Design.IToolboxService the
		// .NET Design API talks to) doesn't depend on it. Instead, the shared WPF Toolbox pad
		// (ICSharpCode.WpfDesign.AddIn.WpfToolbox, which also hosts the XAML designer's palette)
		// is reused for WinForms controls too, via ToolsContent below - dropping onto a WinForms
		// design surface (hosted in the out-of-process FormsDesigner.Host process) goes through
		// THIS IToolboxService instance. The two AddIns deliberately have NO compile-time
		// reference to each other - they coordinate entirely through services registered into the
		// global SD.Services (Base project): WpfToolbox registers ISharedToolboxHost, this class
		// registers IToolboxService, and each resolves the other's contribution by service lookup
		// instead of a direct type reference (see ToolsPad.cs's ISharedToolboxHost doc comment).
		static readonly ToolboxService toolboxService = new ToolboxService();

		static FormsDesignerViewContent()
		{
			SD.Services.AddService(typeof(System.Drawing.Design.IToolboxService), toolboxService);
		}

		bool disposing;
		
		readonly IViewContent primaryViewContent;
		readonly IDesignerLoaderProvider loaderProvider;
		readonly ResourceStore resourceStore;
		FormsDesignerHostClient remoteClient;
		RemoteFormsDesignerControl remoteControl;
		long remoteDocumentVersion;
		// Guards against a superseded async load applying stale state (e.g. the user switched
		// tabs/files again, or edited the source, before an in-flight acquire+open completed) -
		// see doc/technotes/designer-common.md's Design-tab-activation convention.
		long loadGeneration;
		Dictionary<string, string> lastLoadedTexts;
		readonly Stack<Dictionary<string, string>> remoteUndo = new Stack<Dictionary<string, string>>();
			readonly Stack<Dictionary<string, string>> remoteRedo = new Stack<Dictionary<string, string>>();
			readonly DesignerCommandController commands = new DesignerCommandController();
		List<DesignerComponentInfo> remoteClipboard;
		
		readonly DesignerSourceCodeStorage sourceCodeStorage;

		// The in-process DesignerSurface/IDesignerHost has been removed (the WinForms designer
		// is exclusively out-of-process since 2026-08, see doc/technotes/winforms-designer.md).
		// This member is kept only as a degenerate API for legacy consumers such as
		// WixBinding's WixDialogDesigner, which guards on "Host != null" to decide whether a
		// designer is loaded; with no in-process host there is never one.
		public IDesignerHost Host {
			get {
				return null;
			}
		}

		internal bool IsRemoteDesignerLoaded => remoteClient?.IsAlive == true && remoteControl?.State?.Accepted == true;
		internal int RemoteDesignerProcessId => remoteClient?.ProcessId ?? 0;
		internal DesignerSessionState RemoteDesignerState => remoteControl?.State;

		/// <summary>The currently selected component name on the remote design surface.</summary>
		internal string RemoteDesignerSelectedComponent => remoteControl?.SelectedComponentName ?? "";

		/// <summary>Short backend label for the toolbar ("WinForms" / "LibreWinForms").</summary>
		internal string BackendName => remoteControl?.BackendName ?? FormsDesignerHostClient.GetBackendName(GetProjectBackend());

		/// <summary>Surface geometry (frame/selection/handle/element) for integration tests.</summary>
		internal DesignerSurfaceGeometry? RemoteSurfaceGeometry
			=> remoteControl?.SurfaceGeometry();

		internal bool TryGetRemoteComponentScreenBounds(string componentName, out System.Windows.Rect bounds)
		{
			bounds = System.Windows.Rect.Empty;
			return remoteControl?.TryGetComponentScreenBounds(componentName, out bounds) == true;
		}

		internal bool TryGetRemoteTabHeaderScreenBounds(string tabControlName, int tabIndex, out System.Windows.Rect bounds)
		{
			bounds = System.Windows.Rect.Empty;
			return remoteControl?.TryGetTabHeaderScreenBounds(tabControlName, tabIndex, out bounds) == true;
		}

		internal void SetRemoteProperty(string componentName, string propertyName, string value)
		{
			if (!IsRemoteDesignerLoaded)
				throw new InvalidOperationException("The out-of-process WinForms designer is not loaded.");
			ExecuteRemoteEdit(() => remoteClient.SetPropertyAsync(remoteDocumentVersion, componentName, propertyName, value,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult());
		}

		internal void ResetRemoteProperty(string componentName, string propertyName)
		{
			EnsureRemoteDesignerLoaded();
			ExecuteRemoteEdit(() => remoteClient.ResetPropertyAsync(remoteDocumentVersion, componentName, propertyName,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult());
		}

		internal void RenameRemoteComponent(string componentName, string newName)
		{
			EnsureRemoteDesignerLoaded();
			if (componentName == newName) return;
			remoteControl.RenameSelection(componentName, newName);
			try {
				ExecuteRemoteEdit(() => remoteClient.RenameAsync(remoteDocumentVersion, componentName, newName,
					System.Threading.CancellationToken.None).GetAwaiter().GetResult());
			} catch {
				remoteControl.RenameSelection(newName, componentName);
				throw;
			}
		}

		internal void SetRemoteEvent(string componentName, string eventName, string handlerName)
		{
			EnsureRemoteDesignerLoaded();
			ExecuteRemoteEdit(() => remoteClient.SetEventAsync(remoteDocumentVersion, componentName, eventName, handlerName,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult());
			// VS-style: after creating/binding a handler, open the primary source and jump to it.
			if (!String.IsNullOrEmpty(handlerName))
				JumpToHandler(handlerName);
		}

		/// <summary>Locates the generated handler method in the primary source file and jumps to
		/// it, opening/activating the code editor when needed.</summary>
		void JumpToHandler(string handlerName)
		{
			var primary = SourceFiles.FirstOrDefault(item => item.Key != DesignerCodeFile);
			if (primary.Key == null)
				return;
			var text = primary.Value?.Text;
			var offset = text == null ? -1 : text.IndexOf(handlerName + "(", StringComparison.Ordinal);
			if (offset < 0)
				return;
			var line = 1 + text.Take(offset).Count(character => character == '\n');
			if (primary.Key == primaryViewContent.PrimaryFile)
				ShowSourceCode(line);
			else
				FileService.JumpToFilePosition(primary.Key.FileName, line, 1);
		}

		/// <summary>Direct RPC access to the smart-tag/verb pair for DevFlow - bypasses the
		/// chevron glyph and Ctrl+. keyboard shortcut entirely, since both require driving real OS
		/// mouse/keyboard input against a tiny/keyboard-focus-dependent target that proved
		/// unreliable to hit blindly via synthetic screen coordinates (see the 2026-09-05 TabControl
		/// technote entries). This gives tests and manual DevFlow sessions a direct way to exercise
		/// Add Tab/Remove Tab (and any other smart-tag/verb feature) without any of that.</summary>
		internal DesignerSmartTagActions ListRemoteSmartTagActions(string componentName)
		{
			if (!IsRemoteDesignerLoaded)
				throw new InvalidOperationException("The out-of-process WinForms designer is not loaded.");
			return remoteClient.ListSmartTagActionsAsync(remoteDocumentVersion, componentName,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult();
		}

		internal void InvokeRemoteSmartTagMethod(string componentName, int listIndex, int itemIndex)
		{
			if (!IsRemoteDesignerLoaded)
				throw new InvalidOperationException("The out-of-process WinForms designer is not loaded.");
			ExecuteRemoteEdit(() => remoteClient.InvokeSmartTagMethodAsync(remoteDocumentVersion, componentName,
				listIndex, itemIndex, System.Threading.CancellationToken.None).GetAwaiter().GetResult());
		}

		internal DesignerVerbs ListRemoteVerbs(string componentName)
		{
			if (!IsRemoteDesignerLoaded)
				throw new InvalidOperationException("The out-of-process WinForms designer is not loaded.");
			return remoteClient.ListVerbsAsync(remoteDocumentVersion, componentName,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult();
		}

		internal void InvokeRemoteVerb(string componentName, int verbIndex)
		{
			if (!IsRemoteDesignerLoaded)
				throw new InvalidOperationException("The out-of-process WinForms designer is not loaded.");
			ExecuteRemoteEdit(() => remoteClient.InvokeVerbAsync(remoteDocumentVersion, componentName,
				verbIndex, System.Threading.CancellationToken.None).GetAwaiter().GetResult());
		}

		internal void AddRemoteControl(string parentName, string controlType, string componentName, int x, int y)
		{
			if (!IsRemoteDesignerLoaded)
				throw new InvalidOperationException("The out-of-process WinForms designer is not loaded.");
			ExecuteRemoteEdit(() => remoteClient.AddElementAsync(remoteDocumentVersion, parentName,
				new DesignerToolboxItemInfo { TypeName = controlType }, componentName, x, y,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult());
		}

		internal void SetRemoteBounds(string componentName, int x, int y, int width, int height)
		{
			EnsureRemoteDesignerLoaded();
			ExecuteRemoteEdit(() => {
				var result = remoteClient.SetBoundsAsync(remoteDocumentVersion, componentName, x, y, width, height,
					System.Threading.CancellationToken.None).GetAwaiter().GetResult();
				var rootComp = result.Components?.FirstOrDefault(c => c.Name == componentName);
				return result;
			});
		}

		internal void DeleteRemoteComponent(string componentName)
		{
			EnsureRemoteDesignerLoaded();
			ExecuteRemoteEdit(() => remoteClient.DeleteElementsAsync(remoteDocumentVersion, new[] { componentName },
				System.Threading.CancellationToken.None).GetAwaiter().GetResult());
		}

		internal void SetRemoteZOrder(bool bringToFront)
		{
			EnsureRemoteDesignerLoaded();
			var component = SelectedRemoteComponent();
			if (component == null || String.IsNullOrEmpty(component.Parent)) return;
			ExecuteRemoteEdit(() => remoteClient.SetZOrderAsync(remoteDocumentVersion, component.Name, bringToFront,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult());
		}

		internal bool TryExecuteRemoteLayout(CommandID command)
		{
			if (!IsRemoteDesignerLoaded) return false;
			string operation = null;
			if (command.Equals(StandardCommands.AlignToGrid) || command.Equals(StandardCommands.SizeToGrid)) operation = "snap-grid";
			else if (command.Equals(StandardCommands.AlignLeft)) operation = "align-left";
			else if (command.Equals(StandardCommands.AlignRight)) operation = "align-right";
			else if (command.Equals(StandardCommands.AlignTop)) operation = "align-top";
			else if (command.Equals(StandardCommands.AlignBottom)) operation = "align-bottom";
			else if (command.Equals(StandardCommands.AlignHorizontalCenters)) operation = "align-horizontal-centers";
			else if (command.Equals(StandardCommands.AlignVerticalCenters)) operation = "align-vertical-centers";
			else if (command.Equals(StandardCommands.SizeToControl)) operation = "same-size";
			else if (command.Equals(StandardCommands.SizeToControlWidth)) operation = "same-width";
			else if (command.Equals(StandardCommands.SizeToControlHeight)) operation = "same-height";
			else if (command.Equals(StandardCommands.CenterHorizontally)) operation = "center-horizontal";
			else if (command.Equals(StandardCommands.CenterVertically)) operation = "center-vertical";
			else if (command.Equals(StandardCommands.HorizSpaceMakeEqual)) operation = "horizontal-space-equal";
			else if (command.Equals(StandardCommands.HorizSpaceIncrease)) operation = "horizontal-space-increase";
			else if (command.Equals(StandardCommands.HorizSpaceDecrease)) operation = "horizontal-space-decrease";
			else if (command.Equals(StandardCommands.HorizSpaceConcatenate)) operation = "horizontal-space-concatenate";
			else if (command.Equals(StandardCommands.VertSpaceMakeEqual)) operation = "vertical-space-equal";
			else if (command.Equals(StandardCommands.VertSpaceIncrease)) operation = "vertical-space-increase";
			else if (command.Equals(StandardCommands.VertSpaceDecrease)) operation = "vertical-space-decrease";
			else if (command.Equals(StandardCommands.VertSpaceConcatenate)) operation = "vertical-space-concatenate";
			if (operation == null) return false;
			var candidates = remoteControl.SelectedComponentNames
				.Where(name => !remoteControl.IsLocked(name)
					&& remoteControl.State.Components.Any(item => item.Name == name && !String.IsNullOrEmpty(item.Parent))).ToArray();
			var primaryParent = candidates.Length == 0 ? null
				: remoteControl.State.Components.First(item => item.Name == candidates[0]).Parent;
			var names = candidates.Where(name => remoteControl.State.Components.First(item => item.Name == name).Parent == primaryParent).ToArray();
			if (names.Length == 0) return true;
			ExecuteRemoteEdit(() => remoteClient.ApplyLayoutAsync(remoteDocumentVersion, operation, names, 0, 0,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult());
			return true;
		}

		internal void ToggleRemoteLock()
		{
			if (IsRemoteDesignerLoaded) remoteControl.ToggleSelectedLocked();
		}

		/// <summary>The current multi-selection's component names (first is primary).</summary>
		internal string[] RemoteSelectedComponentNames
			=> IsRemoteDesignerLoaded ? remoteControl.SelectedComponentNames : Array.Empty<string>();

		/// <summary>Sets the design-surface selection to the named components (multi-select).</summary>
		internal void SelectRemoteComponents(params string[] names)
		{
			if (IsRemoteDesignerLoaded) remoteControl.SelectComponents(names);
		}

		/// <summary>Moves the current multi-selection by (deltaX, deltaY) design units.</summary>
		internal bool TryNudgeRemoteSelection(int deltaX, int deltaY)
		{
			if (!IsRemoteDesignerLoaded || (deltaX == 0 && deltaY == 0))
				return false;
			MoveRemoteSelection(deltaX, deltaY);
			return true;
		}

		void MoveRemoteSelection(int deltaX, int deltaY)
		{
			if (deltaX == 0 && deltaY == 0) return;
			var state = remoteControl.State;
			var candidates = remoteControl.SelectedComponentNames
				.Where(name => !remoteControl.IsLocked(name))
				.Select(name => state.Components.FirstOrDefault(item => item.Name == name))
				.Where(item => item != null && !String.IsNullOrEmpty(item.Parent)).ToArray();
			if (candidates.Length == 0) return;
			var selected = new HashSet<string>(candidates.Select(item => item.Name), StringComparer.Ordinal);
			var roots = candidates.Where(item => !selected.Contains(item.Parent)).ToArray();
			var parent = roots[0].Parent;
			var names = roots.Where(item => item.Parent == parent).Select(item => item.Name).ToArray();
			ExecuteRemoteEdit(() => remoteClient.ApplyLayoutAsync(remoteDocumentVersion, "move", names, deltaX, deltaY,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult());
		}

		void ExecuteRemoteEdit(Func<DesignerSessionState> edit)
		{
			var before = CaptureRemoteDocuments();
			var state = edit();
			if (!state.Accepted)
				throw new FormsDesignerLoadException(state.Error);
			remoteUndo.Push(before);
			remoteRedo.Clear();
			ApplyRemoteEdit(state);
		}

		void ApplyRemoteEdit(DesignerSessionState state)
		{
			if (!state.Accepted)
				throw new FormsDesignerLoadException(state.Error);
			var rootComp = state.Components?.FirstOrDefault(c => c.Name == remoteControl.SelectedComponentName);
			remoteControl.Show(state);
			UpdateOutline(state);
			SynchronizeRemoteEdits();
			MakeDirty();
		}

		void EnsureRemoteDesignerLoaded()
		{
			if (!IsRemoteDesignerLoaded)
				throw new InvalidOperationException("The out-of-process WinForms designer is not loaded.");
		}

		Dictionary<string, string> CaptureRemoteDocuments() => SourceFiles.ToDictionary(
			item => item.Key.FileName.ToString(), item => item.Value.Text, StringComparer.OrdinalIgnoreCase);

		void RestoreRemoteDocuments(Dictionary<string, string> documents)
		{
			foreach (var source in SourceFiles)
				if (documents.TryGetValue(source.Key.FileName.ToString(), out var text)) source.Value.Text = text;
			var snapshot = CreateRemoteSnapshot(++remoteDocumentVersion);
			var state = remoteClient.UpdateAsync(snapshot, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
			if (!state.Accepted) throw new FormsDesignerLoadException(state.Error);
			remoteControl.Show(state);
			UpdateOutline(state);
			MakeDirty();
		}
		
		public OpenedFile DesignerCodeFile {
			get { return this.sourceCodeStorage.DesignerCodeFile; }
		}
		
		public IDocument PrimaryFileDocument {
			get { return this.sourceCodeStorage[this.PrimaryFile]; }
		}
		
		public ITextSource PrimaryFileContent {
			get { return this.PrimaryFileDocument.CreateSnapshot(); }
		}
		
		public IDocument DesignerCodeFileDocument {
			get {
				if (this.sourceCodeStorage.DesignerCodeFile == null) {
					return null;
				} else {
					return this.sourceCodeStorage[this.sourceCodeStorage.DesignerCodeFile];
				}
			}
		}
		
		public string DesignerCodeFileContent {
			get { return this.DesignerCodeFileDocument.Text; }
			set { this.DesignerCodeFileDocument.Text = value; }
		}
		
		public IDocument GetDocumentForFile(OpenedFile file)
		{
			return this.sourceCodeStorage[file];
		}
		
		public IEnumerable<KeyValuePair<OpenedFile, IDocument>> SourceFiles {
			get { return this.sourceCodeStorage; }
		}
		
		protected DesignerSourceCodeStorage SourceCodeStorage {
			get { return this.sourceCodeStorage; }
		}
		
		public IViewContent PrimaryViewContent {
			get { return this.primaryViewContent; }
		}
		
		protected override string LoadErrorHeaderText {
			get { return StringParser.Parse("${res:ICSharpCode.SharpDevelop.FormDesigner.LoadErrorCheckSourceCodeForErrors}") + Environment.NewLine + Environment.NewLine; }
		}
		
		FormsDesignerViewContent(IViewContent primaryViewContent)
			: base()
			{
				commands.RegisterStandard(() => IsRemoteDesignerLoaded && remoteUndo.Count > 0, UndoCore,
					() => IsRemoteDesignerLoaded && remoteRedo.Count > 0, RedoCore,
					() => SelectedRemoteComponents().Count > 0, DeleteCore);
				this.TabPageText = "${res:FormsDesigner.DesignTabPages.DesignTabPage}";
			
			this.primaryViewContent = primaryViewContent;
			
			this.sourceCodeStorage = new DesignerSourceCodeStorage();
			this.resourceStore = new ResourceStore(this);
			
			FileService.FileRemoving += this.FileServiceFileRemoving;
		}
		
		public FormsDesignerViewContent(IViewContent primaryViewContent, IDesignerLoaderProvider loaderProvider)
			: this(primaryViewContent)
		{
			if (loaderProvider == null)
				throw new ArgumentNullException("loaderProvider");
			
			this.loaderProvider = loaderProvider;
			
			this.Files.Add(this.primaryViewContent.PrimaryFile);
		}
		
		/// <summary>
		/// This constructor allows running in unit test mode with a mock file.
		/// </summary>
		public FormsDesignerViewContent(IViewContent primaryViewContent, OpenedFile mockFile)
			: this(primaryViewContent)
		{
			this.sourceCodeStorage.AddFile(mockFile, Encoding.UTF8);
			this.sourceCodeStorage.DesignerCodeFile = mockFile;
			this.Files.Add(primaryViewContent.PrimaryFile);
		}
		
		bool inMasterLoadOperation;
		
		protected override void LoadInternal(OpenedFile file, System.IO.Stream stream)
		{
			LoggingService.Debug("Forms designer: Load " + file.FileName + "; inMasterLoadOperation=" + this.inMasterLoadOperation);
			
			if (inMasterLoadOperation) {
				
				if (this.sourceCodeStorage.ContainsFile(file)) {
					LoggingService.Debug("Forms designer: Loading " + file.FileName + " in source code storage");
					this.sourceCodeStorage.LoadFile(file, stream);
				} else {
					LoggingService.Debug("Forms designer: Loading " + file.FileName + " in resource store");
					this.resourceStore.Load(file, stream);
				}
				
			} else if (file == this.PrimaryFile || this.sourceCodeStorage.ContainsFile(file)) {

				// Deciding whether a reload is actually needed (vs. reusing the live session)
				// requires the NEW source text first - LoadRemoteDesigner() below makes that call
				// itself once sourceCodeStorage has the fresh content, instead of unconditionally
				// tearing the designer down here before we even know whether anything changed.
				this.inMasterLoadOperation = true;
				
				try {
					
					this.sourceCodeStorage.LoadFile(file, stream);
					
					LoggingService.Debug("Forms designer: Determining designer source files for " + file.FileName);
					OpenedFile newDesignerCodeFile;
					IReadOnlyList<OpenedFile> sourceFiles = loaderProvider.GetSourceFiles(this, out newDesignerCodeFile);
					if (sourceFiles == null || newDesignerCodeFile == null) {
						throw new FormsDesignerLoadException("The designer source files could not be determined.");
					}
					
					// Unload all source files from the view which are no longer in the returned collection
					foreach (OpenedFile f in this.Files.Except(sourceFiles).ToArray()) {
						// Ensure that we only unload source files, but not resource files.
						if (this.sourceCodeStorage.ContainsFile(f)) {
							LoggingService.Debug("Forms designer: Unloading file '" + f.FileName + "' because it no longer belongs to the designed form");
							this.Files.Remove(f);
							this.sourceCodeStorage.RemoveFile(f);
						}
					}
					
					// Load all files which are new in the returned collection
					foreach (OpenedFile f in sourceFiles.Except(this.Files).ToArray()) {
						this.sourceCodeStorage.AddFile(f);
						this.Files.Add(f);
					}
					
					this.sourceCodeStorage.DesignerCodeFile = newDesignerCodeFile;
					
					this.LoadRemoteDesigner();
					
				} finally {
					this.inMasterLoadOperation = false;
				}
				
			} else {
				
				// Loading a resource file
				
				bool mustReload;
				if (this.IsRemoteDesignerLoaded) {
					LoggingService.Debug("Forms designer: Reloading designer because of LoadInternal on resource file");
					this.UnloadRemoteDesigner();
					mustReload = true;
					this.inMasterLoadOperation = true;
				} else {
					mustReload = false;
				}
				
				try {
					LoggingService.Debug("Forms designer: Loading " + file.FileName + " in resource store");
					this.resourceStore.Load(file, stream);
					if (mustReload) {
						this.LoadRemoteDesigner();
					}
				} finally {
					this.inMasterLoadOperation = false;
				}
				
			}
		}
		
		protected override void SaveInternal(OpenedFile file, System.IO.Stream stream)
		{
			LoggingService.Debug("Forms designer: Save " + file.FileName);
			if (hasUnmergedChanges) {
				this.MergeFormChanges();
			}
			if (this.sourceCodeStorage.ContainsFile(file)) {
				this.sourceCodeStorage.SaveFile(file, stream);
			} else {
				this.resourceStore.Save(file, stream);
			}
			if (remoteControl != null && file == PrimaryFile && DesignerCodeFile != null
				&& DesignerCodeFile != file && DesignerCodeFile.IsDirty) {
				DesignerCodeFile.SaveToDisk();
			}
		}
		
		internal void AddResourceFile(OpenedFile file)
		{
			this.Files.Add(file);
		}
		
		IProject GetProjectForFile()
		{
			return SD.ProjectService.FindProjectContainingFile(this.DesignerCodeFile.FileName);
		}

		FormsDesignerBackend GetProjectBackend()
		{
			var project = GetProjectForFile() as MSBuildBasedProject;
			return FormsDesignerHostClient.ResolveBackend(
				project?.GetEvaluatedProperty("UseMicrosoftDesktopRuntime"), "",
				project?.GetEvaluatedProperty("TargetFramework"));
		}
		
		bool hasUnmergedChanges;
		
		void MakeDirty()
		{
			hasUnmergedChanges = true;
			this.DesignerCodeFile.MakeDirty();
			this.resourceStore.MarkResourceFilesAsDirty();
			System.Windows.Input.CommandManager.InvalidateRequerySuggested();
		}
		
		readonly PropertyContainer propertyContainer = new PropertyContainer();

		public PropertyContainer PropertyContainer {
			get {
				return propertyContainer;
			}
		}

		public void ShowHelp()
		{
			// The in-process IDesignerHost has been removed; context help for the WinForms
			// designer is handled inside the out-of-process host.
		}

		static bool RemoteDocumentsUnchanged(Dictionary<string, string> current, Dictionary<string, string> previous)
		{
			if (previous == null || current.Count != previous.Count)
				return false;
			foreach (var pair in current) {
				if (!previous.TryGetValue(pair.Key, out var text) || !String.Equals(text, pair.Value, StringComparison.Ordinal))
					return false;
			}
			return true;
		}

		/// <summary>
		/// Starts (or reuses) the out-of-process WinForms design session. Never blocks the
		/// dispatcher: if a live session already exists and nothing changed since the last
		/// successful load, this returns immediately without any RPC; otherwise it shows the
		/// shared "please wait" chrome (<see cref="DesignerCanvas.SetLoading"/>) and kicks
		/// off the acquire+open round-trip in the background, applying the result only if this is
		/// still the most recent load request when it completes. See
		/// doc/technotes/designer-common.md's Design-tab-activation convention.
		/// </summary>
		void LoadRemoteDesigner()
		{
			var backend = GetProjectBackend();
			var currentTexts = CaptureRemoteDocuments();
			if (IsRemoteDesignerLoaded && RemoteDocumentsUnchanged(currentTexts, lastLoadedTexts)) {
				base.UserContent = remoteControl;
				return;
			}

			var myGeneration = ++loadGeneration;
			// Keep showing the last-rendered canvas (dimmed by the loading overlay) across a
			// reload when one already exists; only fall back to a bare placeholder on this view's
			// very first load.
			DesignerCanvas canvas = remoteControl;
			if (canvas == null) {
				canvas = new DesignerCanvas();
				base.UserContent = canvas;
			}
			canvas.SetLoading(true, "Starting " + FormsDesignerHostClient.GetBackendName(backend) + " design host…");

			var oldClient = remoteClient;
			remoteClient = null;
			// Built here, synchronously, on the dispatcher: it reads AvalonEdit documents, which
			// (like every WPF DependencyObject) can only be touched from the thread that owns
			// them - the async continuation below resumes on a thread-pool thread
			// (ConfigureAwait(false)), so it must not read dispatcher-affine state itself.
			var snapshot = CreateRemoteSnapshot(++remoteDocumentVersion);
			_ = LoadRemoteDesignerAsync(myGeneration, currentTexts, snapshot, canvas, oldClient, backend);
		}

		async System.Threading.Tasks.Task LoadRemoteDesignerAsync(long generation, Dictionary<string, string> texts,
			DesignerDocumentSnapshot snapshot, DesignerCanvas loadingCanvas, FormsDesignerHostClient oldClient,
			FormsDesignerBackend backend)
		{
			oldClient?.Dispose();
			FormsDesignerHostClient client;
			DesignerSessionState state;
			try {
				client = await FormsDesignerHostClient.AcquireSharedAsync("", "", System.Threading.CancellationToken.None,
					FormsDesignerHostClient.LocateChildDll(backend)).ConfigureAwait(false);
				state = await client.OpenAsync(snapshot, System.Threading.CancellationToken.None).ConfigureAwait(false);
			} catch (Exception exception) {
				SD.MainThread.InvokeAsyncAndForget(() => {
					if (generation != loadGeneration || disposing) return;
					LoggingService.Error(exception);
					loadingCanvas.SetLoading(false);
					loadingCanvas.ShowStatusBar = true;
					loadingCanvas.StatusText = "The WinForms designer could not be started: " + exception.Message;
				});
				return;
			}

			SD.MainThread.InvokeAsyncAndForget(() => {
				if (generation != loadGeneration || disposing) {
					// A newer load (or the file/view closing) superseded this one while the
					// out-of-process handshake was still in flight - discard the result.
					client.Dispose();
					return;
				}
				OutputChannel.Write("WinForms", "Design host opened for " + PrimaryFile.FileName);
				if (!state.Accepted) {
					client.Dispose();
					loadingCanvas.SetLoading(false);
					loadingCanvas.ShowStatusBar = true;
					loadingCanvas.StatusText = "Failed to load designer: " + state.Error;
					return;
				}
				remoteClient = client;
				remoteClient.HostExited += RemoteHostExited;
				remoteClient.Recovered += RemoteHostRecovered;
				remoteControl = new RemoteFormsDesignerControl(remoteClient, FormsDesignerHostClient.GetBackendName(backend));
				remoteControl.ToolboxDrop += RemoteToolboxDrop;
				remoteControl.BoundsChanged += RemoteBoundsChanged;
				remoteControl.SelectionMoveRequested += (sender, e) => {
					try { MoveRemoteSelection(e.DeltaX, e.DeltaY); }
					catch (Exception exception) { LoggingService.Error(exception); MessageService.ShowError(exception.Message); }
				};
				remoteControl.ReorderRequested += (sender, e) => {
					try {
						ExecuteRemoteEdit(() => remoteClient.ReorderToolStripItemAsync(remoteDocumentVersion, e.ComponentName, e.TargetIndex,
							System.Threading.CancellationToken.None).GetAwaiter().GetResult());
					} catch (Exception exception) { LoggingService.Error(exception); MessageService.ShowError(exception.Message); }
				};
				remoteControl.RenameRequested += (sender, e) => {
					try { RenameRemoteComponent(e.ComponentName, e.NewName); }
					catch (Exception exception) { LoggingService.Error(exception); MessageService.ShowError(exception.Message); }
				};
				remoteControl.DeleteRequested += RemoteDeleteRequested;
				remoteControl.DefaultEventRequested += RemoteDefaultEventRequested;
				remoteControl.SelectionChanged += RemoteSelectionChanged;
				remoteControl.RestartRequested += RemoteRestartRequested;
				remoteControl.SmartTagRequested += RemoteSmartTagRequested;
				remoteControl.ContextMenuRequested += RemoteContextMenuRequested;
				remoteControl.ToolStripInsertRequested += RemoteToolStripInsertRequested;
				remoteControl.ToolStripTypeHereCommitted += RemoteToolStripTypeHereCommitted;
				outline.SelectionCommitted += OnOutlineSelectionCommitted;
				remoteControl.Show(state);
				UpdateOutline(state);
				ActivateOutlinePadOnce();
				base.UserContent = remoteControl;
				hasUnmergedChanges = false;
				lastLoadedTexts = texts;
			});
		}

		bool outlinePadActivated;

		/// <summary>
		/// Shows the Document Outline pad the first time a form is designed, so the control
		/// tree is visible without the user having to open the pad manually.
		/// </summary>
		void ActivateOutlinePadOnce()
		{
			if (outlinePadActivated)
				return;
			outlinePadActivated = true;
			try {
				SD.Workbench.GetPad("ICSharpCode.SharpDevelop.Gui.OutlinePad")?.BringPadToFront();
			} catch (Exception ex) {
				LoggingService.Debug("Forms designer: could not activate the Outline pad: " + ex.Message);
			}
		}

		readonly DocumentOutlineControl outline = new DocumentOutlineControl();
		readonly DesignerSelectionController shellSelection = new DesignerSelectionController();

		public object OutlineContent {
			get { return outline; }
		}

		void OnOutlineSelectionCommitted(object sender, EventArgs e)
		{
			// Outline -> design surface: the surface owns selection; route the pick through the
			// same single-selection path as a surface click.
			if (outline.SelectedNode is { } node && remoteControl != null) {
				shellSelection.Select(node.Id);
				remoteControl.SelectComponent(node.Id);
			}
		}

		/// <summary>Rebuilds the Document Outline from the protocol's element tree. Falls back
		/// to building the tree from the flat component list when the child did not report one
		/// (older host binaries).</summary>
		void UpdateOutline(DesignerSessionState state)
		{
			var root = state.Tree ?? BuildOutlineTree(state.Components);
			shellSelection.UpdateTree(root);
			outline.SetRoots(shellSelection.Roots);
		}

		static DesignerElementNode BuildOutlineTree(List<DesignerComponentInfo> components)
		{
			var byName = components.ToDictionary(item => item.Name, StringComparer.Ordinal);
			var root = components.FirstOrDefault(item => String.IsNullOrEmpty(item.Parent));
			if (root == null)
				return null;
			return BuildOutlineNode(root, byName);
		}

		static DesignerElementNode BuildOutlineNode(DesignerComponentInfo component, Dictionary<string, DesignerComponentInfo> byName)
		{
			return new DesignerElementNode {
				Id = component.Name,
				Name = component.Name,
				Type = component.Type,
				X = component.X,
				Y = component.Y,
				Width = component.Width,
				Height = component.Height,
				IsDesignable = true,
				Children = byName.Values
					.Where(item => item.Parent == component.Name)
					.Select(item => BuildOutlineNode(item, byName))
					.ToList()
			};
		}

		DesignerDocumentSnapshot CreateRemoteSnapshot(long version)
		{
			var project = GetProjectForFile();
			var snapshot = new DesignerDocumentSnapshot {
				Version = version,
				ProjectFileName = project?.FileName.ToString() ?? "",
				TargetFramework = (project as MSBuildBasedProject)?.GetEvaluatedProperty("TargetFramework") ?? "",
				ProjectAssemblyPath = GetManagedAssemblyPath(project),
				PrimaryFileName = PrimaryFileName,
				DesignerFileName = DesignerCodeFile?.FileName.ToString() ?? "",
				Language = PrimaryFileName.ToString().EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ? "VisualBasic" : "CSharp"
			};
			// Copy-local references are already built metadata. Passing them to the child lets it
			// resolve a custom Control/Component selected in the toolbox without loading project
			// assemblies in the IDE or synchronously invoking MSBuild reference resolution.
			var outputDirectory = Path.GetDirectoryName(snapshot.ProjectAssemblyPath);
			if (!string.IsNullOrEmpty(outputDirectory) && Directory.Exists(outputDirectory))
				snapshot.ReferencedAssemblyPaths.AddRange(Directory.EnumerateFiles(outputDirectory, "*.dll")
					.Where(path => !string.Equals(path, snapshot.ProjectAssemblyPath, StringComparison.OrdinalIgnoreCase)));
			foreach (var source in SourceFiles) {
				snapshot.Files.Add(new DesignerSourceFileSnapshot {
					FileName = source.Key.FileName.ToString(),
					Kind = source.Key == DesignerCodeFile ? "Designer" : "Source",
					Text = source.Value.Text
				});
			}
			foreach (var resource in Files.Where(file => !sourceCodeStorage.ContainsFile(file))) {
				using (var stream = resource.OpenRead())
				using (var memory = new System.IO.MemoryStream()) {
					stream.CopyTo(memory);
					snapshot.Files.Add(new DesignerSourceFileSnapshot {
						FileName = resource.FileName.ToString(), Kind = "Resource",
						Base64 = Convert.ToBase64String(memory.ToArray())
					});
				}
			}
			return snapshot;
		}

		/// <summary>OutputAssemblyFullPath can point at the apphost (an extensionless executable
		/// on Unix, or a ".exe" native shim on Windows) instead of the managed assembly; the
		/// out-of-process host needs the managed ".dll", so prefer the sibling when it exists.</summary>
		static string GetManagedAssemblyPath(IProject project)
		{
			var path = project?.OutputAssemblyFullPath.ToString() ?? "";
			if (String.IsNullOrEmpty(path) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				return path;
			var dll = System.IO.Path.ChangeExtension(path, ".dll");
			return System.IO.File.Exists(dll) ? dll : path;
		}

		void UnloadRemoteDesigner()
		{
			if (remoteControl != null) {
				remoteControl = null;
				base.UserContent = null;
			}
			OutputChannel.Write("WinForms", "Design host disposed");
			remoteClient?.Dispose();
			remoteClient = null;
		}

		void RemoteHostExited(object sender, EventArgs e)
		{
			var log = remoteClient?.ChildLog;
			SD.MainThread.InvokeAsyncAndForget(() => {
				if (disposing || remoteControl == null) return;
				remoteControl.ShowDisconnected("The WinForms designer process exited unexpectedly."
					+ (String.IsNullOrWhiteSpace(log) ? "" : Environment.NewLine + Environment.NewLine + log.Trim()));
				propertyContainer.Clear();
			});
		}

		void RemoteHostRecovered(object sender, DesignerSessionState state)
		{
			SD.MainThread.InvokeAsyncAndForget(() => {
				if (disposing || !ReferenceEquals(sender, remoteClient) || remoteControl == null) return;
				if (!state.Accepted) {
					remoteControl.ShowDisconnected("The WinForms designer could not restore the document: " + state.Error);
					return;
				}
				remoteControl.Show(state);
				UpdateOutline(state);
				OutputChannel.Write("WinForms", "Design host recovered for " + PrimaryFile.FileName);
			});
		}

		void RemoteRestartRequested(object sender, EventArgs e)
		{
			var previousControl = remoteControl;
			try {
				LoadRemoteDesigner();
			} catch (Exception exception) {
				LoggingService.Error(exception);
				if (remoteControl == null && previousControl != null) {
					remoteControl = previousControl;
					base.UserContent = previousControl;
				}
				remoteControl?.ShowDisconnected("The WinForms designer could not restart: " + exception.Message);
			}
		}

		void RemoteToolboxDrop(object sender, RemoteToolboxDropEventArgs e)
		{
			var state = RemoteDesignerState;
			var root = state?.Components.FirstOrDefault(component => String.IsNullOrEmpty(component.Parent));
			if (root == null)
				return;
			var shortType = e.ControlType[(e.ControlType.LastIndexOf('.') + 1)..];
			var prefix = Char.ToLowerInvariant(shortType[0]) + shortType.Substring(1);
			var suffix = 1;
			while (state.Components.Any(component => component.Name == prefix + suffix)) suffix++;
			try {
				AddRemoteControl(String.IsNullOrEmpty(e.ParentName) ? root.Name : e.ParentName, e.ControlType, prefix + suffix, e.X, e.Y);
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		void RemoteBoundsChanged(object sender, RemoteBoundsChangedEventArgs e)
		{
			try {
				SetRemoteBounds(e.ComponentName, e.X, e.Y, e.Width, e.Height);
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		void RemoteDeleteRequested(object sender, RemoteComponentEventArgs e)
		{
			try {
				Delete();
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		void RemoteDefaultEventRequested(object sender, RemoteComponentEventArgs e)
		{
			try {
				DesignerSessionState activated = null;
				ExecuteRemoteEdit(() => activated = remoteClient.ActivateDefaultEventAsync(remoteDocumentVersion, e.ComponentName,
					System.Threading.CancellationToken.None).GetAwaiter().GetResult());
				var handler = activated.Components.FirstOrDefault(item => item.Name == e.ComponentName)?.Events
					.FirstOrDefault(item => !String.IsNullOrEmpty(item.Handler))?.Handler;
				if (!String.IsNullOrEmpty(handler)) {
					var primary = SourceFiles.FirstOrDefault(item => item.Key != DesignerCodeFile);
					var offset = primary.Value?.Text.IndexOf(handler + "(", StringComparison.Ordinal) ?? -1;
					if (offset >= 0) {
						var line = 1 + primary.Value.Text.Take(offset).Count(character => character == '\n');
						if (primary.Key == primaryViewContent.PrimaryFile)
							ShowSourceCode(line);
						else
							FileService.JumpToFilePosition(primary.Key.FileName, line, 1);
					}
				}
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		/// <summary>Smart-tag chevron clicked: fetch the component's DesignerActionList items
		/// AND its designer verbs (both read-only fetches - not an edit, so neither goes through
		/// <see cref="ExecuteRemoteEdit"/>) and show them together in a small popup anchored on the
		/// chevron glyph. Verbs (e.g. TabControlDesigner's "Add Tab"/"Remove Tab") are real VS's
		/// right-click context-menu items, not smart-tag content - this client has no separate
		/// right-click menu of its own, so they are folded into the same popup rather than building
		/// a whole second UI surface for a single component type.</summary>
		async void RemoteSmartTagRequested(object sender, RemoteSmartTagRequestedEventArgs e)
		{
			try {
				var actions = await remoteClient.ListSmartTagActionsAsync(remoteDocumentVersion, e.ComponentName,
					System.Threading.CancellationToken.None);
				var verbs = await remoteClient.ListVerbsAsync(remoteDocumentVersion, e.ComponentName,
					System.Threading.CancellationToken.None);
				var visibleVerbs = verbs.Accepted ? verbs.Items.Where(item => item.Visible).ToList() : new List<DesignerVerbInfo>();
				if ((!actions.Accepted || actions.Items.Count == 0) && visibleVerbs.Count == 0)
					return;
				ShowSmartTagPopup(e.Anchor, e.ComponentName, actions.Accepted ? actions.Items : new List<DesignerSmartTagActionInfo>(), visibleVerbs);
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		/// <summary>The one smart-tag/toolstrip-insert popup currently open, if any. A
		/// <c>Popup</c> with <c>StaysOpen=false</c> only auto-closes on a click OUTSIDE its own
		/// bounds; leaving a previous instance open (each call here used to create a brand new
		/// local <c>Popup</c> and never track or close the last one) left an invisible input-
		/// capturing surface behind that ate the next click anywhere else in the IDE - including
		/// the Outline pad - which read as "only the first/root item still responds". Explicitly
		/// closing the previous popup before opening a new one, and clearing this field when a
		/// popup closes itself, keeps at most one ever open.</summary>
		System.Windows.Controls.Primitives.Popup activeDesignerPopup;

		void CloseActiveDesignerPopup()
		{
			if (activeDesignerPopup != null) {
				activeDesignerPopup.IsOpen = false;
				activeDesignerPopup = null;
			}
		}

		void ShowSmartTagPopup(System.Windows.FrameworkElement anchor, string componentName, IReadOnlyList<DesignerSmartTagActionInfo> items, IReadOnlyList<DesignerVerbInfo> verbs)
		{
			CloseActiveDesignerPopup();
			var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(6) };
			var popup = new System.Windows.Controls.Primitives.Popup {
				PlacementTarget = anchor,
				Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
				StaysOpen = false,
				AllowsTransparency = true,
				Child = new System.Windows.Controls.Border {
					Background = System.Windows.SystemColors.WindowBrush,
					BorderBrush = System.Windows.SystemColors.ActiveBorderBrush,
					BorderThickness = new Thickness(1),
					Child = panel
				}
			};
			foreach (var item in items) {
				switch (item.Kind) {
				case "Header":
					panel.Children.Add(new System.Windows.Controls.TextBlock {
						Text = item.DisplayName, FontWeight = System.Windows.FontWeights.Bold, Margin = new Thickness(2, 4, 2, 2) });
					break;
				case "Text":
					panel.Children.Add(new System.Windows.Controls.TextBlock { Text = item.DisplayName, Margin = new Thickness(2) });
					break;
				case "Method": {
					var button = new System.Windows.Controls.Button { Content = item.DisplayName, Margin = new Thickness(2), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left };
					var captured = item;
					button.Click += async (buttonSender, buttonArgs) => {
						popup.IsOpen = false;
						await InvokeSmartTagMethodAsync(componentName, captured);
					};
					panel.Children.Add(button);
					break;
				}
				case "Property": {
					var row = new System.Windows.Controls.DockPanel { Margin = new Thickness(2) };
					row.Children.Add(new System.Windows.Controls.TextBlock {
						Text = item.DisplayName, Width = 90, VerticalAlignment = System.Windows.VerticalAlignment.Center });
					var owner = String.IsNullOrEmpty(item.PropertyOwnerElementId) ? componentName : item.PropertyOwnerElementId;
					System.Windows.FrameworkElement editor;
					if (item.TypeName == "System.Boolean") {
						var check = new System.Windows.Controls.CheckBox { IsChecked = String.Equals(item.Value, "True", StringComparison.OrdinalIgnoreCase) };
						check.Click += async (checkSender, checkArgs) =>
							await CommitSmartTagPropertyAsync(owner, item.MemberName, check.IsChecked == true ? "True" : "False");
						editor = check;
					} else if (item.IsEnum && item.AllowedValues.Count > 0) {
						var combo = new System.Windows.Controls.ComboBox { Width = 120 };
						foreach (var allowed in item.AllowedValues) combo.Items.Add(allowed);
						combo.SelectedItem = item.Value;
						combo.SelectionChanged += async (comboSender, comboArgs) => {
							if (combo.SelectedItem is string selected)
								await CommitSmartTagPropertyAsync(owner, item.MemberName, selected);
						};
						editor = combo;
					} else {
						var text = new System.Windows.Controls.TextBox { Text = item.Value, Width = 120 };
						text.LostFocus += async (textSender, textArgs) =>
							await CommitSmartTagPropertyAsync(owner, item.MemberName, text.Text);
						text.KeyDown += async (textSender, keyArgs) => {
							if (keyArgs.Key == System.Windows.Input.Key.Enter)
								await CommitSmartTagPropertyAsync(owner, item.MemberName, text.Text);
						};
						editor = text;
					}
					row.Children.Add(editor);
					panel.Children.Add(row);
					break;
				}
				}
			}
			if (items.Count > 0 && verbs.Count > 0)
				panel.Children.Add(new System.Windows.Controls.Separator { Margin = new Thickness(0, 4, 0, 4) });
			foreach (var verb in verbs) {
				var button = new System.Windows.Controls.Button {
					Content = verb.Text, Margin = new Thickness(2), IsEnabled = verb.Enabled,
					HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
					ToolTip = String.IsNullOrEmpty(verb.Description) ? null : verb.Description
				};
				var captured = verb;
				button.Click += async (buttonSender, buttonArgs) => {
					popup.IsOpen = false;
					await InvokeVerbAsync(componentName, captured);
				};
				panel.Children.Add(button);
			}
			popup.Closed += (popupSender, popupArgs) => { if (activeDesignerPopup == popup) activeDesignerPopup = null; };
			activeDesignerPopup = popup;
			popup.IsOpen = true;
		}

		async System.Threading.Tasks.Task InvokeSmartTagMethodAsync(string componentName, DesignerSmartTagActionInfo item)
		{
			try {
				DesignerSessionState result = null;
				ExecuteRemoteEdit(() => result = remoteClient.InvokeSmartTagMethodAsync(remoteDocumentVersion, componentName,
					item.ListIndex, item.ItemIndex, System.Threading.CancellationToken.None).GetAwaiter().GetResult());
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		/// <summary>One designer verb as the context menu renders it, prepared here so
		/// DesignerVerbSubmenuBuilder - which the AddIn tree calls synchronously - never has to make
		/// the RPC itself.</summary>
		internal sealed class VerbMenuEntry
		{
			public string Text { get; set; }
			public string Description { get; set; }
			public bool Enabled { get; set; }
			public Action Invoke { get; set; }
		}

		/// <summary>Verbs for the component that was last right-clicked, read by
		/// DesignerVerbSubmenuBuilder while the menu is being built.</summary>
		internal IReadOnlyList<VerbMenuEntry> PendingVerbMenuEntries { get; private set; }

		/// <summary>Right-click on the design surface: show the designer context menu the AddIn tree
		/// declares, with the clicked component's designer verbs (TabControlDesigner's Add
		/// Tab/Remove Tab, and whatever any other component's designer exposes) spliced in where
		/// DesignerVerbSubmenuBuilder sits.
		///
		/// The menus at /SharpDevelop/FormsDesigner/ContextMenus/* were declared for the old
		/// in-process designer and then orphaned by the move out of process - nothing built them any
		/// more. Building them here rather than assembling a menu by hand is what brings back View
		/// Code, Bring to Front/Send to Back, Align to Grid, Show Tab Order, Lock Controls,
		/// Cut/Copy/Paste/Delete and Properties, all of which already have working commands, AND
		/// restores the extension point: an AddIn can contribute an item by declaring it.
		///
		/// Verbs are a Microsoft-backend feature (design/list-verbs answers Accepted=false on the
		/// portable fork), so on Libre the verb group is simply absent rather than an error.</summary>
		async void RemoteContextMenuRequested(object sender, RemoteComponentEventArgs e)
		{
			try {
				CloseActiveDesignerPopup();
				var path = await PrepareContextMenuAsync(e.ComponentName);
				// ShowContextMenu both builds and opens, and expands menu builders synchronously as
				// it goes - which is why PrepareContextMenuAsync had to await the verbs first.
				// Fully qualified: ICSharpCode.Core.WinForms has a MenuService of its own, and this
				// file sees both.
				ICSharpCode.Core.Presentation.MenuService.ShowContextMenu(remoteControl, this, path);
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		/// <summary>Prepares the verb entries for <paramref name="componentName"/> and returns which
		/// declared menu applies to it.</summary>
		/// <remarks>
		/// Shared by the right-click handler and DescribeContextMenuAsync so the described menu is
		/// the same menu that opens, not a reimplementation that could drift from it.
		/// </remarks>
		async System.Threading.Tasks.Task<string> PrepareContextMenuAsync(string componentName)
		{
			var components = RemoteDesignerState?.Components?.ToList();
			PendingVerbMenuEntries = String.IsNullOrEmpty(componentName)
				? null
				: await GatherVerbMenuEntriesAsync(componentName, components);
			return MenuPathFor(componentName, components);
		}

		/// <summary>Which declared menu a component gets. The root form gets the container menu,
		/// matching real VS: no z-order or Cut/Copy on the thing that owns the surface.</summary>
		static string MenuPathFor(string componentName, List<DesignerComponentInfo> components)
		{
			var clicked = String.IsNullOrEmpty(componentName) ? null
				: components?.FirstOrDefault(item => item.Name == componentName);
			var isRoot = clicked == null || String.IsNullOrEmpty(clicked.Parent);
			return isRoot
				? "/SharpDevelop/FormsDesigner/ContextMenus/ContainerMenu"
				: "/SharpDevelop/FormsDesigner/ContextMenus/SelectionMenu";
		}

		/// <summary>Builds the context menu for a component WITHOUT opening it and reports the item
		/// labels, so the menu's content can actually be asserted.</summary>
		/// <remarks>
		/// This exists because a WPF ContextMenu is its own top-level window and is therefore
		/// invisible to both of DevFlow's observation channels - it appears in no screenshot and in
		/// no ui/tree. Verifying it by opening it is impossible; only a human can see it. So the
		/// feature is split at the popup boundary: this builds the menu through exactly the same
		/// path the right-click uses, minus the opening, and returns what a viewer would read.
		/// </remarks>
		/// <remarks>
		/// Synchronous on purpose. DevFlow dispatches actions ON the UI thread, so an action that
		/// awaited the verb RPC and then blocked for the result would deadlock: the continuation
		/// would be posted back to the very thread waiting on it. The RPC is therefore pushed to the
		/// thread pool - where it has no SynchronizationContext to return to - and only the menu
		/// construction, which must be on the UI thread, runs here. The right-click path keeps its
		/// plain await instead, because there the continuation belongs on the UI thread and nothing
		/// blocks it.
		/// </remarks>
		internal IReadOnlyList<string> DescribeContextMenu(string componentName)
		{
			var components = RemoteDesignerState?.Components?.ToList();
			var entries = String.IsNullOrEmpty(componentName)
				? null
				: System.Threading.Tasks.Task.Run(
					() => GatherVerbMenuEntriesAsync(componentName, components)).GetAwaiter().GetResult();
			PendingVerbMenuEntries = entries;
			var path = MenuPathFor(componentName, components);
			var labels = new List<string>();
			foreach (var item in ICSharpCode.Core.Presentation.MenuService.CreateMenuItems(remoteControl, this, path, "ContextMenu")) {
				switch (item) {
					case System.Windows.Controls.Separator:
						labels.Add("---");
						break;
					case System.Windows.Controls.MenuItem menuItem:
						labels.Add(StringParser.Parse(menuItem.Header?.ToString() ?? "?"));
						break;
					default:
						// Menu items the AddIn tree produced that are neither - recorded by type so a
						// regression shows up as an unexpected entry rather than a silent gap.
						labels.Add("<" + item?.GetType().Name + ">");
						break;
				}
			}
			return labels;
		}

		/// <summary>Lists the clicked component's verbs and those of its containers, collapsed by
		/// DesignerVerbMenuPlanner.</summary>
		/// <remarks>
		/// The container walk is the reason Add Tab is reachable at all: those verbs belong to the
		/// TabControl's designer, but a TabControl's surface is almost entirely covered by its pages,
		/// so right-clicking the page area - the obvious gesture for "add another tab" - resolves to
		/// a TabPage, whose own designer publishes no verbs. Confirmed against the live designer:
		/// list-verbs returns Add Tab/Remove Tab for tabControl1 and an empty list for tabPage1.
		/// </remarks>
		async System.Threading.Tasks.Task<IReadOnlyList<VerbMenuEntry>> GatherVerbMenuEntriesAsync(
			string componentName, List<DesignerComponentInfo> components)
		{
			// Innermost-first, which is the order Plan needs to let the nearest owner win a
			// duplicate verb name.
			var gathered = new List<(string Owner, DesignerVerbInfo Verb)>();
			var chain = DesignerVerbMenuPlanner.ComponentAndItsContainers(componentName,
				name => components?.FirstOrDefault(item => item.Name == name)?.Parent);
			foreach (var owner in chain) {
				DesignerVerbs verbs = null;
				try {
					verbs = await remoteClient.ListVerbsAsync(remoteDocumentVersion, owner,
						System.Threading.CancellationToken.None);
				} catch (Exception exception) {
					// A component whose designer refuses to enumerate verbs must still get a menu -
					// everything else the .addin declares is still meaningful.
					LoggingService.Warn("Forms designer: list-verbs failed for " + owner + ": " + exception.Message);
				}
				if (verbs?.Accepted != true)
					continue;
				foreach (var verb in verbs.Items.Where(item => item.Visible))
					gathered.Add((owner, verb));
			}
			var entries = new List<VerbMenuEntry>();
			foreach (var planned in DesignerVerbMenuPlanner.Plan(gathered
				.Select(entry => new DesignerVerbCandidate(entry.Owner, entry.Verb.Text, entry.Verb.Index)))) {
				var source = gathered.First(entry => entry.Owner == planned.OwnerName && entry.Verb.Text == planned.Text);
				var capturedVerb = source.Verb;
				var capturedOwner = planned.OwnerName;
				entries.Add(new VerbMenuEntry {
					Text = planned.Text,
					Description = source.Verb.Description,
					Enabled = source.Verb.Enabled,
					// Invoking targets the OWNER, not what was right-clicked - the whole point of
					// the container walk is that those differ.
					Invoke = async () => await InvokeVerbAsync(capturedOwner, capturedVerb)
				});
			}
			return entries;
		}

		async System.Threading.Tasks.Task InvokeVerbAsync(string componentName, DesignerVerbInfo verb)
		{
			try {
				DesignerSessionState result = null;
				ExecuteRemoteEdit(() => result = remoteClient.InvokeVerbAsync(remoteDocumentVersion, componentName,
					verb.Index, System.Threading.CancellationToken.None).GetAwaiter().GetResult());
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		async System.Threading.Tasks.Task CommitSmartTagPropertyAsync(string ownerElementId, string propertyName, string value)
		{
			try {
				ExecuteRemoteEdit(() => remoteClient.SetPropertyAsync(remoteDocumentVersion, ownerElementId, propertyName, value,
					System.Threading.CancellationToken.None).GetAwaiter().GetResult());
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
			await System.Threading.Tasks.Task.CompletedTask;
		}

		/// <summary>ToolStrip/StatusStrip/MenuStrip "insert new item" chevron clicked: show a small
		/// dropdown of item type names appropriate to the strip kind.</summary>
		void RemoteToolStripInsertRequested(object sender, RemoteToolStripInsertRequestedEventArgs e)
		{
			try {
				CloseActiveDesignerPopup();
				var itemTypes = e.ComponentType switch {
					// Mirrors LibreWinForms/real WinForms System.Windows.Forms.Design.ToolStripDesignerUtils'
					// s_newItemTypesForXxx lists exactly (order included - "default item is
					// determined by being first in the list").
					"System.Windows.Forms.MenuStrip" => new[] { "ToolStripMenuItem", "ToolStripComboBox", "ToolStripTextBox" },
					"System.Windows.Forms.StatusStrip" => new[] { "ToolStripStatusLabel", "ToolStripProgressBar", "ToolStripDropDownButton", "ToolStripSplitButton" },
					_ => new[] { "ToolStripButton", "ToolStripLabel", "ToolStripSplitButton", "ToolStripDropDownButton", "ToolStripSeparator", "ToolStripComboBox", "ToolStripTextBox", "ToolStripProgressBar" }
				};
				var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(4) };
				var popup = new System.Windows.Controls.Primitives.Popup {
					PlacementTarget = e.Anchor,
					Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
					StaysOpen = false,
					AllowsTransparency = true,
					Child = new System.Windows.Controls.Border {
						Background = System.Windows.SystemColors.WindowBrush,
						BorderBrush = System.Windows.SystemColors.ActiveBorderBrush,
						BorderThickness = new Thickness(1),
						Child = panel
					}
				};
				foreach (var itemType in itemTypes) {
					// Icon column + text column, matching the real WinForms designer's
					// NewItemsContextMenuStrip (ToolStripDesignerUtils.GetNewItemDropDown) row
					// shape. The icon is the type's REAL embedded WinForms toolbox icon (VS's own
					// convention, System.Drawing.ToolboxBitmapAttribute.GetImageFromResource via
					// design/get-type-icon) - not a VS chrome icon from the VS2017 Image Library,
					// which has no WinForms-control icons at all.
					var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(2) };
					var iconHost = new System.Windows.Controls.Border {
					Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0),
					Child = new System.Windows.Shapes.Rectangle { Width = 9, Height = 9, Fill = System.Windows.Media.Brushes.SeaGreen }
				};
					row.Children.Add(iconHost);
					_ = LoadTypeIconAsync("System.Windows.Forms." + itemType, iconHost);
					row.Children.Add(new System.Windows.Controls.TextBlock { Text = ToolStripItemDisplayName(itemType), VerticalAlignment = System.Windows.VerticalAlignment.Center });
					var button = new System.Windows.Controls.Button { Content = row, Margin = new Thickness(0), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left, Padding = new Thickness(4, 2, 8, 2) };
					button.Click += (buttonSender, buttonArgs) => {
						popup.IsOpen = false;
						AddRemoteToolStripItem(e.ComponentName, itemType);
					};
					panel.Children.Add(button);
				}
				popup.Closed += (popupSender, popupArgs) => { if (activeDesignerPopup == popup) activeDesignerPopup = null; };
				activeDesignerPopup = popup;
				popup.IsOpen = true;
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		/// <summary>"ToolStripSplitButton" -&gt; "SplitButton", matching the real designer's
		/// NewItemsContextMenuStrip row text (it labels by the CLR type name minus the
		/// "ToolStrip" prefix - "MenuItem" also comes out this way for ToolStripMenuItem).</summary>
		static string ToolStripItemDisplayName(string itemType) =>
			itemType.StartsWith("ToolStrip", StringComparison.Ordinal) ? itemType["ToolStrip".Length..] : itemType;

		/// <summary>Fetches the real WinForms toolbox icon (design/get-type-icon) for
		/// <paramref name="fullTypeName"/> and shows it inside <paramref name="host"/>. Leaves
		/// the host's existing placeholder-swatch content in place (never clears it first) if the
		/// RPC fails, returns no icon (e.g. unsupported on the Libre backend, or a type with no
		/// embedded resource), or the popup closes before the fetch completes.</summary>
		async System.Threading.Tasks.Task LoadTypeIconAsync(string fullTypeName, System.Windows.Controls.Border host)
		{
			try {
				if (!IsRemoteDesignerLoaded) return;
				var png = await remoteClient.GetTypeIconAsync(fullTypeName, System.Threading.CancellationToken.None);
				if (String.IsNullOrEmpty(png)) return;
				var bytes = Convert.FromBase64String(png);
				var bitmap = new System.Windows.Media.Imaging.BitmapImage();
				using (var stream = new MemoryStream(bytes)) {
					bitmap.BeginInit();
					bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
					bitmap.StreamSource = stream;
					bitmap.EndInit();
				}
				bitmap.Freeze();
				var image = new System.Windows.Controls.Image { Source = bitmap, Width = 16, Height = 16, SnapsToDevicePixels = true };
				System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
				host.Child = image;
			} catch {
				// Keep whatever placeholder the caller already put in `host` - a missing icon is
				// cosmetic, never worth surfacing as an error dialog.
			}
		}

		void AddRemoteToolStripItem(string stripName, string itemType)
		{
			try {
				var existing = new HashSet<string>((RemoteDesignerState?.Components ?? new List<DesignerComponentInfo>())
					.Select(item => item.Name), StringComparer.Ordinal);
				var baseName = Char.ToLowerInvariant(itemType[0]) + itemType.Substring(1);
				var newItemId = baseName;
				for (var index = 1; existing.Contains(newItemId); index++)
					newItemId = baseName + index;
				ExecuteRemoteEdit(() => remoteClient.AddToolStripItemAsync(remoteDocumentVersion, stripName, itemType, "", newItemId,
					System.Threading.CancellationToken.None).GetAwaiter().GetResult());
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		/// <summary>A name typed into a MenuStrip's "Type Here" cell: create the item and give it
		/// that text. Both RPCs run inside ONE ExecuteRemoteEdit so the pair is a single undo step,
		/// the way committing the real template node is.</summary>
		void RemoteToolStripTypeHereCommitted(object sender, RemoteToolStripTypeHereEventArgs e)
		{
			try {
				var newItemId = NextRemoteComponentName(e.ItemTypeName);
				ExecuteRemoteEdit(() => {
					remoteClient.AddToolStripItemAsync(remoteDocumentVersion, e.ComponentName, e.ItemTypeName, e.ParentItemId,
						newItemId, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
					return remoteClient.SetPropertyAsync(remoteDocumentVersion, newItemId, "Text", e.Text,
						System.Threading.CancellationToken.None).GetAwaiter().GetResult();
				});
			} catch (Exception exception) {
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		/// <summary>An unused component name derived from the type's short name, matching the
		/// designer's own "button1, button2, ..." convention.</summary>
		string NextRemoteComponentName(string itemTypeName)
		{
			var existing = new HashSet<string>((RemoteDesignerState?.Components ?? new List<DesignerComponentInfo>())
				.Select(item => item.Name), StringComparer.Ordinal);
			var shortName = itemTypeName.Substring(itemTypeName.LastIndexOf('.') + 1);
			var baseName = Char.ToLowerInvariant(shortName[0]) + shortName.Substring(1);
			var name = baseName + "1";
			for (var index = 2; existing.Contains(name); index++)
				name = baseName + index;
			return name;
		}

		/// <summary>Mirrors the selection into the child's real ISelectionService so the genuine
		/// design-time chrome renders into the next frame (see
		/// DesignerHostService.SetSelection). Deliberately NOT routed through ExecuteRemoteEdit:
		/// selecting something is not a document edit and must not push an undo entry.</summary>
		void SyncRemoteSelection(string[] elementIds)
		{
			if (!IsRemoteDesignerLoaded)
				return;
			try {
				var state = remoteClient.SetSelectionAsync(remoteDocumentVersion, elementIds,
					System.Threading.CancellationToken.None).GetAwaiter().GetResult();
				if (state.Accepted)
					remoteControl.Show(state);
			} catch (Exception exception) {
				// A failed selection mirror must not break selecting things in the IDE.
				LoggingService.Warn("Forms designer: could not mirror selection into the child: " + exception.Message);
			}
		}

		void RemoteSelectionChanged(object sender, EventArgs e)
		{
			var component = RemoteDesignerState?.Components.FirstOrDefault(item => item.Name == remoteControl.SelectedComponentName);
			if (component == null) {
				propertyContainer.Clear();
				return;
			}
			var proxies = remoteControl.SelectedComponentNames.Select(name => RemoteDesignerState?.Components.FirstOrDefault(item => item.Name == name)).Where(item => item != null).Select(item => (object)new RemoteComponentPropertyProxy(this, item)).ToArray();
			propertyContainer.SelectedObject = proxies.Length > 1 ? new DesignerMultiPropertyAdapter(proxies) : proxies.FirstOrDefault();
			shellSelection.Select(remoteControl.SelectedComponentNames);
			// Design surface -> Document Outline: mirror the selection without re-triggering
			// the outline->surface path (same element, no-op anyway).
			outline.SelectNodeById(component.Name);
			System.Windows.Input.CommandManager.InvalidateRequerySuggested();
			// Last, because it re-renders: tell the child's real selection service, which is what
			// brings up the genuine ToolStrip/menu editing chrome in the returned frame.
			SyncRemoteSelection(remoteControl.SelectedComponentNames.ToArray());
		}

		public virtual void MergeFormChanges()
		{
			SD.AnalyticsMonitor.TrackFeature(typeof(FormsDesignerViewContent), "Save");
			if (this.HasLoadError || !IsRemoteDesignerLoaded) {
				LoggingService.Debug("Forms designer: Cannot merge form changes because the designer is not loaded successfully or not loaded at all");
				return;
			} else if (this.DesignerCodeFile == null) {
				throw new InvalidOperationException("Cannot merge form changes without a designer code file.");
			}
			MergeRemoteFormChanges();
		}

		void MergeRemoteFormChanges()
		{
			if (DesignerCodeFile == null)
				throw new InvalidOperationException("Cannot merge form changes without a designer code file.");
			SynchronizeRemoteEdits();
			hasUnmergedChanges = false;
		}

		DesignerEditSet SynchronizeRemoteEdits()
		{
			var edits = remoteClient.FlushAsync(remoteDocumentVersion, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
			if (edits.BaseVersion != remoteDocumentVersion)
				throw new InvalidOperationException("The WinForms designer host returned edits for a stale document version.");
			foreach (var edit in edits.Files) {
				if (edit.Kind.Equals("Resource", StringComparison.OrdinalIgnoreCase)) continue;
				var source = SourceFiles.FirstOrDefault(item => FileUtility.IsEqualFileName(item.Key.FileName, edit.FileName));
				if (source.Key == null)
					throw new InvalidOperationException("The WinForms designer host returned an edit for an unknown file: " + edit.FileName);
				if (!String.Equals(source.Value.Text, edit.Text, StringComparison.Ordinal)) {
					source.Value.Text = edit.Text;
					// The Forms designer keeps its own source storage while the primary
					// code tab owns a separate AvalonEdit document. Keep the live editor in
					// sync too; otherwise saving Form1.cs after an event-handler generation
					// serializes its stale editor text and silently loses the new method.
					var editor = source.Key == primaryViewContent.PrimaryFile
						? primaryViewContent.GetService<ITextEditor>()
						: source.Key.CurrentView?.GetService<ITextEditor>();
					if (editor != null && !String.Equals(editor.Document.Text, edit.Text, StringComparison.Ordinal))
						editor.Document.Text = edit.Text;
					// A host edit can modify both Form1.Designer.cs and the primary
					// Form1.cs (for example when an Events-row double-click creates its
					// handler).  Mark each affected OpenedFile dirty so Ctrl+S / Save All
					// persists every changed source file, not only the designer document.
					source.Key.MakeDirty();
				}
			}
			return edits;
		}

		/// <remarks>if lineNumber = 0 no jump is performed, but the active view content changes.</remarks>
		public void ShowSourceCode(int lineNumber = 0)
		{
			this.WorkbenchWindow.ActiveViewContent = this.PrimaryViewContent;
			if (lineNumber <= 0) return;
			ITextEditor editor = this.primaryViewContent.GetService<ITextEditor>();
			if (editor != null) {
				editor.JumpTo(lineNumber, 1);
			}
		}

		/*
		public void ShowSourceCode(IComponent component, EventDescriptor edesc, string eventMethodName)
		{
			int position;
			string file;
			bool eventCreated = generator.InsertComponentEvent(component, edesc, eventMethodName, "", out file, out position);
			if (eventCreated) {
				if (FileUtility.IsEqualFileName(file, this.primaryViewContent.PrimaryFileName)) {
					ShowSourceCode(position);
				} else {
					FileService.JumpToFilePosition(file, position, 0);
				}
			}
		}

		public ICollection GetCompatibleMethods(EventDescriptor edesc)
		{
			return generator.GetCompatibleMethods(edesc);
		}
		*/

		public override void Dispose()
		{
			disposing = true;
			try {
				// base.Dispose() is called first because it may trigger a call
				// to SaveInternal which requires the designer to be loaded.
				base.Dispose();
			} finally {
				FileService.FileRemoving -= this.FileServiceFileRemoving;
				
				this.UnloadRemoteDesigner();
				
				this.resourceStore.Dispose();
				
				base.UserContent = null;
			}
		}

		protected void UpdatePropertyPad()
		{
			// The modern PropertyContainer (doc/technotes/ilspy.md docking replacement) only
			// tracks SelectedObject(s) - it has no equivalent of the old Host/SelectableObjects
			// pair, which fed a separate "pick any component in this designer" dropdown in the
			// SharpDevelop 4-era property grid toolbar. Forwarding the current selection (below)
			// is what actually drives the property grid; that dropdown is not ported. The remote
			// designer drives the pad via RemoteSelectionChanged; this method only serves as a
			// refresh hook for legacy consumers such as WixBinding.
			if (remoteControl != null && remoteControl.SelectedComponentName is { Length: > 0 } selectedName
				&& RemoteDesignerState?.Components.FirstOrDefault(item => item.Name == selectedName) is { } component) {
				propertyContainer.SelectedObject = new RemoteComponentPropertyProxy(this, component);
				System.Windows.Input.CommandManager.InvalidateRequerySuggested();
			} else {
				propertyContainer.Clear();
			}
		}

		sealed class RemoteComponentPropertyProxy : ICustomTypeDescriptor, IPropertyGridEventSource, IEventBindingHost
		{
			readonly FormsDesignerViewContent owner;
			string name;
			string text;
			int x;
			int y;
			int width;
			int height;
			readonly List<DesignerPropertyInfo> remoteProperties;
			readonly List<DesignerEventInfo> remoteEvents;

			public RemoteComponentPropertyProxy(FormsDesignerViewContent owner, DesignerComponentInfo component)
			{
				this.owner = owner;
				name = component.Name;
				ComponentType = component.Type;
				text = component.Text;
				x = component.X;
				y = component.Y;
				width = component.Width;
				height = component.Height;
				remoteProperties = component.Properties ?? new List<DesignerPropertyInfo>();
				remoteEvents = component.Events ?? new List<DesignerEventInfo>();
			}

			[Category("Design")]
			public string Name {
				get => name;
				set {
					owner.RenameRemoteComponent(name, value);
					name = value;
				}
			}

			[Category("Design"), ReadOnly(true)]
			public string ComponentType { get; }

			[Category("Appearance")]
			public string Text {
				get => text;
				set {
					owner.SetRemoteProperty(name, nameof(Text), value ?? "");
					text = value ?? "";
				}
			}

			[Category("Layout")]
			public int X { get => x; set { owner.SetRemoteBounds(name, value, y, width, height); x = value; } }

			[Category("Layout")]
			public int Y { get => y; set { owner.SetRemoteBounds(name, x, value, width, height); y = value; } }

			[Category("Layout")]
			public int Width { get => width; set { owner.SetRemoteBounds(name, x, y, value, height); width = value; } }

			[Category("Layout")]
			public int Height { get => height; set { owner.SetRemoteBounds(name, x, y, width, value); height = value; } }

			AttributeCollection ICustomTypeDescriptor.GetAttributes() => AttributeCollection.Empty;
			string ICustomTypeDescriptor.GetClassName() => ComponentType;
			string ICustomTypeDescriptor.GetComponentName() => Name;
			TypeConverter ICustomTypeDescriptor.GetConverter() => new TypeConverter();
			EventDescriptor ICustomTypeDescriptor.GetDefaultEvent() => null;
			PropertyDescriptor ICustomTypeDescriptor.GetDefaultProperty() => null;
			object ICustomTypeDescriptor.GetEditor(Type editorBaseType) => null;
			EventDescriptorCollection ICustomTypeDescriptor.GetEvents() => GetRemoteEventDescriptors();
			EventDescriptorCollection ICustomTypeDescriptor.GetEvents(Attribute[] attributes) => GetRemoteEventDescriptors();
			PropertyDescriptorCollection ICustomTypeDescriptor.GetProperties() => GetRemotePropertyDescriptors();
			PropertyDescriptorCollection ICustomTypeDescriptor.GetProperties(Attribute[] attributes) => GetRemotePropertyDescriptors();
			object ICustomTypeDescriptor.GetPropertyOwner(PropertyDescriptor pd) => this;

			// IPropertyGridEventSource: handler names live in the out-of-process host, so the
			// Events view reads and writes them through the designer RPC instead of in-memory.
			string IPropertyGridEventSource.GetEventHandler(string eventName)
				=> remoteEvents.FirstOrDefault(item => item.Name == eventName)?.Handler ?? "";

			void IPropertyGridEventSource.SetEventHandler(string eventName, string handlerName)
			{
				var remoteEvent = remoteEvents.FirstOrDefault(item => item.Name == eventName);
				if (remoteEvent == null) return;
				owner.SetRemoteEvent(name, remoteEvent.Name, handlerName ?? "");
				remoteEvent.Handler = handlerName ?? "";
			}

			// IEventBindingHost: VS-style double-click on an Events row creates the conventional
			// <component>_<event> handler, binds it, and persists through the host session.
			void IEventBindingHost.BindEvent(string eventName)
			{
				var remoteEvent = remoteEvents.FirstOrDefault(item => item.Name == eventName);
				if (remoteEvent == null || !String.IsNullOrEmpty(remoteEvent.Handler))
					return; // unknown event, or already bound (a second double-click is a no-op)
				var handlerName = name + "_" + eventName;
				owner.SetRemoteEvent(name, remoteEvent.Name, handlerName);
				remoteEvent.Handler = handlerName;
			}

			PropertyDescriptorCollection GetRemotePropertyDescriptors()
			{
				var descriptors = TypeDescriptor.GetProperties(typeof(RemoteComponentPropertyProxy)).Cast<PropertyDescriptor>().ToList();
				var fixedNames = new HashSet<string>(descriptors.Select(item => item.Name), StringComparer.Ordinal);
				foreach (var property in remoteProperties.Where(item => !fixedNames.Contains(item.Name)))
					descriptors.Add(new RemotePropertyDescriptor(owner, name, property));
				return new PropertyDescriptorCollection(descriptors.ToArray(), true);
			}

			// The WinForms events live in the dedicated Events view (TypeDescriptor.GetEvents),
			// not in the property list - unlike the legacy in-process adapter, which used to
			// surface them as an "Events" property category.
			EventDescriptorCollection GetRemoteEventDescriptors()
			{
				var descriptors = new EventDescriptor[remoteEvents.Count];
				for (var i = 0; i < remoteEvents.Count; i++)
					descriptors[i] = new RemoteEventDescriptor(owner, name, remoteEvents[i]);
				return new EventDescriptorCollection(descriptors, true);
			}
		}

		sealed class RemoteEventDescriptor : EventDescriptor, IPropertyGridEventTypeName
		{
			readonly FormsDesignerViewContent owner;
			readonly string componentName;
			readonly DesignerEventInfo remoteEvent;

			public RemoteEventDescriptor(FormsDesignerViewContent owner, string componentName, DesignerEventInfo remoteEvent)
				: base(remoteEvent.Name, new Attribute[] { new CategoryAttribute(String.IsNullOrEmpty(remoteEvent.Category) ? "Events" : remoteEvent.Category) })
			{
				this.owner = owner;
				this.componentName = componentName;
				this.remoteEvent = remoteEvent;
			}

			public override string DisplayName => remoteEvent.Name;
			public override string Description => remoteEvent.HandlerTypeName;
			public override Type ComponentType => typeof(RemoteComponentPropertyProxy);
			public override Type EventType => typeof(EventHandler);
			public override bool IsMulticast => true;

			/// <summary>The delegate type reported by the out-of-process host for this event.</summary>
			public string HandlerTypeName => String.IsNullOrEmpty(remoteEvent.HandlerTypeName) ? "EventHandler" : remoteEvent.HandlerTypeName;

			public override void AddEventHandler(object component, Delegate handler)
				=> SetHandlerName(handler?.Method.Name ?? "");

			public override void RemoveEventHandler(object component, Delegate handler)
				=> SetHandlerName("");

			void SetHandlerName(string handlerName)
			{
				owner.SetRemoteEvent(componentName, remoteEvent.Name, handlerName);
				remoteEvent.Handler = handlerName;
			}
		}

		sealed class RemotePropertyDescriptor : PropertyDescriptor
		{
			readonly FormsDesignerViewContent owner;
			readonly string componentName;
			readonly DesignerPropertyInfo property;
			readonly Type propertyType;

			public RemotePropertyDescriptor(FormsDesignerViewContent owner, string componentName, DesignerPropertyInfo property)
				: base(property.Name, new Attribute[] { new CategoryAttribute(property.Category), new DescriptionAttribute(property.Description ?? ""), new ReadOnlyAttribute(property.IsReadOnly) })
			{
				this.owner = owner;
				this.componentName = componentName;
				this.property = property;
				propertyType = property.TypeName switch {
					"System.Boolean" => typeof(bool), "System.Byte" => typeof(byte), "System.Int16" => typeof(short),
					"System.Int32" => typeof(int), "System.Int64" => typeof(long), "System.Single" => typeof(float),
					"System.Double" => typeof(double), "System.Decimal" => typeof(decimal), _ => typeof(string)
				};
			}

			public override Type ComponentType => typeof(RemoteComponentPropertyProxy);
			public override string DisplayName => String.IsNullOrEmpty(property.DisplayName) ? property.Name : property.DisplayName;
			public override bool IsReadOnly => property.IsReadOnly;
			public override Type PropertyType => propertyType;
			public override bool CanResetValue(object component) => property.ShouldSerialize && !property.IsReadOnly;
			public override object GetValue(object component)
			{
				if (property.IsNull) return null;
				if (propertyType == typeof(string)) return property.Value;
				return Convert.ChangeType(property.Value, propertyType, System.Globalization.CultureInfo.InvariantCulture);
			}
			public override void ResetValue(object component)
			{
				owner.ResetRemoteProperty(componentName, property.Name);
				property.ShouldSerialize = false;
				OnValueChanged(component, EventArgs.Empty);
			}
			public override void SetValue(object component, object value)
			{
				var serialized = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
				owner.SetRemoteProperty(componentName, property.Name, serialized);
				property.Value = serialized;
				property.IsNull = value == null;
				OnValueChanged(component, EventArgs.Empty);
			}
			public override bool ShouldSerializeValue(object component) => property.ShouldSerialize;
		}

		#region IUndoHandler implementation
		public bool EnableUndo {
			get {
					return commands.CanExecute("Undo");
			}
		}
		public bool EnableRedo {
			get {
					return commands.CanExecute("Redo");
			}
		}
			public virtual void Undo() => commands.Execute("Undo");
			bool UndoCore()
			{
				remoteRedo.Push(CaptureRemoteDocuments());
				RestoreRemoteDocuments(remoteUndo.Pop());
				return true;
			}

			public virtual void Redo() => commands.Execute("Redo");
			bool RedoCore()
			{
				remoteUndo.Push(CaptureRemoteDocuments());
				RestoreRemoteDocuments(remoteRedo.Pop());
				return true;
		}
		#endregion

		#region IClipboardHandler implementation
		public bool EnableCut {
			get {
				return IsRemoteDesignerLoaded && SelectedRemoteComponent()?.Parent?.Length > 0;
			}
		}

		public bool EnableCopy {
			get {
				return IsRemoteDesignerLoaded && SelectedRemoteComponent()?.Parent?.Length > 0;
			}
		}

		const string ComponentClipboardFormat = "CF_DESIGNERCOMPONENTS";
		public bool EnablePaste {
			get {
				return IsRemoteDesignerLoaded && remoteClipboard?.Count > 0;
			}
		}

		public bool EnableDelete {
			get {
				return IsRemoteDesignerLoaded && SelectedRemoteComponent()?.Parent?.Length > 0;
			}
		}

		public bool EnableSelectAll {
			get {
				return IsRemoteDesignerLoaded;
			}
		}

		public void Cut()
		{
			remoteClipboard = SelectedRemoteComponents();
			if (remoteClipboard.Count == 0) return;
			DeleteRemoteComponents(remoteClipboard);
		}

		public void Copy()
		{
			var selected = SelectedRemoteComponents();
			if (selected.Count > 0) remoteClipboard = selected;
		}

		public void Paste()
		{
			if (remoteClipboard?.Count > 0) {
				var state = RemoteDesignerState;
				var root = state.Components.First(component => String.IsNullOrEmpty(component.Parent)).Name;
				var nameMap = new Dictionary<string, string>(StringComparer.Ordinal);
				foreach (var component in remoteClipboard) {
					var shortType = component.Type[(component.Type.LastIndexOf('.') + 1)..];
					var prefix = Char.ToLowerInvariant(shortType[0]) + shortType.Substring(1);
					var suffix = 1;
					while (state.Components.Any(item => item.Name == prefix + suffix) || nameMap.Values.Contains(prefix + suffix)) suffix++;
					nameMap[component.Name] = prefix + suffix;
				}
				ExecuteRemoteEdit(() => {
					DesignerSessionState latest = state;
					foreach (var component in remoteClipboard.OrderBy(item => ClipboardDepth(item, remoteClipboard))) {
						var parent = nameMap.TryGetValue(component.Parent, out var copiedParent) ? copiedParent
							: state.Components.Any(item => item.Name == component.Parent) ? component.Parent : root;
						latest = remoteClient.AddElementAsync(remoteDocumentVersion, parent,
							new DesignerToolboxItemInfo { TypeName = component.Type }, nameMap[component.Name],
							component.X + 10, component.Y + 10, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
						foreach (var property in component.Properties.Where(CanCopyRemoteProperty))
							latest = remoteClient.SetPropertyAsync(remoteDocumentVersion, nameMap[component.Name], property.Name, property.Value,
								System.Threading.CancellationToken.None).GetAwaiter().GetResult();
					}
					return latest;
				});
			}
		}

			public void Delete()
			{
				commands.Execute("Delete");
			}

			bool DeleteCore() { DeleteRemoteComponents(SelectedRemoteComponents()); return true; }

		DesignerComponentInfo SelectedRemoteComponent()
		{
			var selectedName = remoteControl?.SelectedComponentName;
			return String.IsNullOrEmpty(selectedName) ? null
				: RemoteDesignerState?.Components?.FirstOrDefault(component => component.Name == selectedName);
		}

		List<DesignerComponentInfo> SelectedRemoteComponents() => remoteControl?.SelectedComponentNames
			.Select(name => RemoteDesignerState.Components.FirstOrDefault(component => component.Name == name))
			.Where(component => component != null && !String.IsNullOrEmpty(component.Parent)).ToList()
			?? new List<DesignerComponentInfo>();

		void DeleteRemoteComponents(List<DesignerComponentInfo> components)
		{
			if (components.Count == 0) return;
			var selectedNames = new HashSet<string>(components.Select(item => item.Name), StringComparer.Ordinal);
			var roots = components.Where(item => !selectedNames.Contains(item.Parent)).ToArray();
			ExecuteRemoteEdit(() => {
				if (roots.Length == 0) return RemoteDesignerState;
				return remoteClient.DeleteElementsAsync(remoteDocumentVersion, roots.Select(item => item.Name).ToArray(),
					System.Threading.CancellationToken.None).GetAwaiter().GetResult();
			});
		}

		static int ClipboardDepth(DesignerComponentInfo component, List<DesignerComponentInfo> components)
		{
			var depth = 0;
			var parent = component.Parent;
			while (components.FirstOrDefault(item => item.Name == parent) is { } ancestor) {
				depth++;
				parent = ancestor.Parent;
			}
			return depth;
		}

		static bool CanCopyRemoteProperty(DesignerPropertyInfo property)
		{
			if (!property.ShouldSerialize || property.IsReadOnly || property.IsNull
				|| property.Name is "Name" or "Location" or "Size" or "Bounds" or "Parent") return false;
			var type = property.TypeName;
			return type == "System.String" || type == "System.Boolean" || type == "System.Char"
				|| type == "System.Byte" || type == "System.SByte" || type == "System.Int16" || type == "System.UInt16"
				|| type == "System.Int32" || type == "System.UInt32" || type == "System.Int64" || type == "System.UInt64"
				|| type == "System.Single" || type == "System.Double" || type == "System.Decimal"
				|| type == "System.Drawing.Point" || type == "System.Drawing.Size" || type == "System.Drawing.Color"
				|| property.IsEnum;
		}

		public void SelectAll()
		{
			if (IsRemoteDesignerLoaded) {
				remoteControl.SelectAllComponents();
			}
		}
		#endregion

		#region Tab Order Handling
		bool tabOrderMode = false;
		public virtual bool IsTabOrderMode {
			get {
				return tabOrderMode;
			}
		}

		public virtual void ShowTabOrder()
		{
			if (!IsTabOrderMode && IsRemoteDesignerLoaded) {
				remoteControl.SetTabOrderMode(true);
				tabOrderMode = true;
			}
		}

		public virtual void HideTabOrder()
		{
			if (IsTabOrderMode && remoteControl != null) {
				remoteControl.SetTabOrderMode(false);
				tabOrderMode = false;
			}
		}
		#endregion

		protected void MergeAndUnloadDesigner()
		{
			propertyContainer.Clear();
			if (!this.HasLoadError) {
				MergeFormChanges();
			}
			UnloadRemoteDesigner();
		}

		protected void ReloadDesignerFromMemory()
		{
			using(MemoryStream ms = new MemoryStream(this.sourceCodeStorage.GetFileEncoding(this.DesignerCodeFile).GetBytes(this.DesignerCodeFileContent), false)) {
				this.Load(this.DesignerCodeFile, ms);
			}
			
			UpdatePropertyPad();
		}

		public virtual object ToolsContent {
			// See SharedToolboxAccess's doc comment - the shared palette's "winforms" scope must
			// be seeded (touching WpfDesign.AddIn's facade via reflection) before the Tools pad
			// mounts this content, or a pure WinForms session's pad would have nothing to show.
			// This mirrors how WinUIXamlDesignerViewContent.ToolsContent constructs its facade
			// eagerly, so the same single ListBox (Base's SharedToolbox) is what the pad mounts
			// regardless of which designer opened the file.
			get {
				return SharedToolboxAccess.ToolboxControl;
			}
		}

		void FileServiceFileRemoving(object sender, FileCancelEventArgs e)
		{
			if (!e.Cancel) {
				this.CheckForDesignerCodeFileDeletion(e);
			}
		}

		void CheckForDesignerCodeFileDeletion(FileCancelEventArgs e)
		{
			OpenedFile file;
			
			if (e.IsDirectory) {
				file = this.Files.SingleOrDefault(
					f => FileUtility.IsBaseDirectory(e.FileName, f.FileName)
				);
			} else {
				file = this.Files.SingleOrDefault(
					f => FileUtility.IsEqualFileName(f.FileName, e.FileName)
				);
			}
			
			if (file == null || file == this.PrimaryFile)
				return;
			
			LoggingService.Info("Forms designer: Handling deletion of open designer code file '" + file.FileName + "'");
			
			if (file == this.sourceCodeStorage.DesignerCodeFile) {
				this.UnloadRemoteDesigner();
				this.sourceCodeStorage.DesignerCodeFile = null;
			}
			
			// When any of our designer code files is deleted,
			// remove the file from the file list so that
			// the primary view is not closed because of this event.
			this.Files.Remove(file);
			this.sourceCodeStorage.RemoveFile(file);
		}
	}
}
