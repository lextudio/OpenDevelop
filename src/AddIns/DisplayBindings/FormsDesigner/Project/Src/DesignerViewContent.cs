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
using ICSharpCode.SharpDevelop.Designer.Remote;
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
		// (ICSharpCode.WpfDesign.AddIn.WpfToolbox, already built for the XAML designer/editor) is
		// reused for WinForms controls too, via ToolsContent below - dropping onto a WinForms
		// design surface (hosted in the out-of-process FormsDesigner.Host process) goes through
		// THIS IToolboxService instance. Registered into the global SD.Services below so
		// WpfToolbox (a different AddIn, which must not take a compile-time reference to this one
		// to avoid a project-reference cycle - FormsDesigner already depends on WpfDesign.AddIn
		// for ToolsContent) can reach this exact instance via
		// SD.Services.GetService(typeof(System.Drawing.Design.IToolboxService)) instead of a
		// direct type reference.
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
		readonly Stack<Dictionary<string, string>> remoteUndo = new Stack<Dictionary<string, string>>();
		readonly Stack<Dictionary<string, string>> remoteRedo = new Stack<Dictionary<string, string>>();
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

		/// <summary>Surface geometry (frame/selection/resize-handle) for integration tests.</summary>
		internal (Rect Frame, Rect Selection, System.Windows.Point Handle)? RemoteSurfaceGeometry
			=> remoteControl?.SurfaceGeometry();

		internal bool TryGetRemoteComponentScreenBounds(string componentName, out System.Windows.Rect bounds)
		{
			bounds = System.Windows.Rect.Empty;
			return remoteControl?.TryGetComponentScreenBounds(componentName, out bounds) == true;
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
			ExecuteRemoteEdit(() => remoteClient.SetBoundsAsync(remoteDocumentVersion, componentName, x, y, width, height,
				System.Threading.CancellationToken.None).GetAwaiter().GetResult());
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
				
				if (this.IsRemoteDesignerLoaded) {
					this.UnloadRemoteDesigner();
				}
				
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

		void LoadRemoteDesigner()
		{
			UnloadRemoteDesigner();
			remoteClient = FormsDesignerHostClient.StartAsync("", "", System.Threading.CancellationToken.None).GetAwaiter().GetResult();
			remoteClient.HostExited += RemoteHostExited;
			var snapshot = CreateRemoteSnapshot(++remoteDocumentVersion);
			var state = remoteClient.OpenAsync(snapshot, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
			if (!state.Accepted) throw new FormsDesignerLoadException(state.Error);
			remoteControl = new RemoteFormsDesignerControl(remoteClient);
			remoteControl.ToolboxDrop += RemoteToolboxDrop;
			remoteControl.BoundsChanged += RemoteBoundsChanged;
			remoteControl.SelectionMoveRequested += (sender, e) => {
				try { MoveRemoteSelection(e.DeltaX, e.DeltaY); }
				catch (Exception exception) { LoggingService.Error(exception); MessageService.ShowError(exception.Message); }
			};
			remoteControl.DeleteRequested += RemoteDeleteRequested;
			remoteControl.DefaultEventRequested += RemoteDefaultEventRequested;
			remoteControl.SelectionChanged += RemoteSelectionChanged;
			remoteControl.RestartRequested += RemoteRestartRequested;
			outline.SelectionCommitted += OnOutlineSelectionCommitted;
			remoteControl.Show(state);
			UpdateOutline(state);
			ActivateOutlinePadOnce();
			base.UserContent = remoteControl;
			hasUnmergedChanges = false;
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

		public object OutlineContent {
			get { return outline; }
		}

		void OnOutlineSelectionCommitted(object sender, EventArgs e)
		{
			// Outline -> design surface: the surface owns selection; route the pick through the
			// same single-selection path as a surface click.
			if (outline.SelectedNode is { } node && remoteControl != null)
				remoteControl.SelectComponent(node.Id);
		}

		/// <summary>Rebuilds the Document Outline from the protocol's element tree. Falls back
		/// to building the tree from the flat component list when the child did not report one
		/// (older host binaries).</summary>
		void UpdateOutline(DesignerSessionState state)
		{
			outline.SetRoot(state.Tree ?? BuildOutlineTree(state.Components));
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

		void RemoteSelectionChanged(object sender, EventArgs e)
		{
			var component = RemoteDesignerState?.Components.FirstOrDefault(item => item.Name == remoteControl.SelectedComponentName);
			if (component == null) {
				propertyContainer.Clear();
				return;
			}
			propertyContainer.SelectedObject = new RemoteComponentPropertyProxy(this, component);
			// Design surface -> Document Outline: mirror the selection without re-triggering
			// the outline->surface path (same element, no-op anyway).
			outline.SelectNodeById(component.Name);
			System.Windows.Input.CommandManager.InvalidateRequerySuggested();
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
				source.Value.Text = edit.Text;
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
				return IsRemoteDesignerLoaded && remoteUndo.Count > 0;
			}
		}
		public bool EnableRedo {
			get {
				return IsRemoteDesignerLoaded && remoteRedo.Count > 0;
			}
		}
		public virtual void Undo()
		{
			if (IsRemoteDesignerLoaded && remoteUndo.Count > 0) {
				remoteRedo.Push(CaptureRemoteDocuments());
				RestoreRemoteDocuments(remoteUndo.Pop());
			}
		}

		public virtual void Redo()
		{
			if (IsRemoteDesignerLoaded && remoteRedo.Count > 0) {
				remoteUndo.Push(CaptureRemoteDocuments());
				RestoreRemoteDocuments(remoteRedo.Pop());
			}
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
			DeleteRemoteComponents(SelectedRemoteComponents());
		}

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
			// See the toolboxService field's own doc comment - this resolves the shared WPF
			// Toolbox pad (WpfDesign.AddIn's WpfToolbox) via SD.Services, not a direct reference.
			get {
				var host = SD.Services.GetService(typeof(ISharedToolboxHost)) as ISharedToolboxHost;
				return host?.ToolboxControl;
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
