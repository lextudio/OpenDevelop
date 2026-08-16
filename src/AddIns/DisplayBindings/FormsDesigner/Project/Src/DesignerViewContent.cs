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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.WinForms;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.FormsDesigner.Services;
using ICSharpCode.FormsDesigner.UndoRedo;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.FormsDesigner.OutOfProcess;

namespace ICSharpCode.FormsDesigner
{
	public class FormsDesignerViewContent : AbstractViewContentHandlingLoadErrors, IClipboardHandler, IUndoHandler, IHasPropertyContainer, IContextHelpProvider, IToolsHost, IFileDocumentProvider
	{
		// The SideBar-backed drag-from-toolbox panel (ToolboxProvider.FormsDesignerSideBar) stays
		// unused - Services.ToolboxService (the real System.Drawing.Design.IToolboxService the
		// .NET Design API talks to) doesn't depend on it. Instead, the shared WPF Toolbox pad
		// (ICSharpCode.WpfDesign.AddIn.WpfToolbox, already built for the XAML designer/editor) is
		// reused for WinForms controls too, via ToolsContent below - dropping onto a WinForms
		// DesignSurface (hosted in a WindowsFormsHost) goes through THIS IToolboxService instance,
		// not WpfDesign's own CreateComponentTool/DesignItem machinery, since WinForms' real
		// ParentControlDesigner.OnDragEnter/OnDragDrop is what actually creates the component.
		// Registered into the global SD.Services below so WpfToolbox (a different AddIn, which
		// must not take a compile-time reference to this one to avoid a project-reference cycle -
		// FormsDesigner already depends on WpfDesign.AddIn for ToolsContent) can reach this exact
		// instance via SD.Services.GetService(typeof(System.Drawing.Design.IToolboxService))
		// instead of a direct type reference.
		static readonly ToolboxService toolboxService = new ToolboxService();

		static FormsDesignerViewContent()
		{
			SD.Services.AddService(typeof(System.Drawing.Design.IToolboxService), toolboxService);
		}

		readonly Control pleaseWaitLabel = new Label() { Text = StringParser.Parse("${res:Global.PleaseWait}"), TextAlign=ContentAlignment.MiddleCenter };
		DesignSurface designSurface;
		bool disposing;
		Timer timer = new Timer { Interval = 200 };
		
		readonly IViewContent primaryViewContent;
		readonly IDesignerLoaderProvider loaderProvider;
		DesignerLoader loader;
		readonly ResourceStore resourceStore;
		FormsDesignerUndoEngine undoEngine;
		TypeResolutionService typeResolutionService;
		FormsDesignerHostClient remoteClient;
		RemoteFormsDesignerControl remoteControl;
		long remoteDocumentVersion;
		readonly Stack<Dictionary<string, string>> remoteUndo = new Stack<Dictionary<string, string>>();
		readonly Stack<Dictionary<string, string>> remoteRedo = new Stack<Dictionary<string, string>>();
		List<DesignerComponentInfo> remoteClipboard;
		
		readonly DesignerSourceCodeStorage sourceCodeStorage;
		
		readonly Dictionary<Type, TypeDescriptionProvider> addedTypeDescriptionProviders = new Dictionary<Type, TypeDescriptionProvider>();
		
		protected DesignSurface DesignSurface {
			get {
				return designSurface;
			}
		}
		
		public IDesignerHost Host {
			get {
				if (designSurface == null)
					return null;
				return (IDesignerHost)designSurface.GetService(typeof(IDesignerHost));
			}
		}

		internal bool IsRemoteDesignerLoaded => remoteClient?.IsAlive == true && remoteControl?.State?.Accepted == true;
		internal int RemoteDesignerProcessId => remoteClient?.ProcessId ?? 0;
		internal DesignerSessionState RemoteDesignerState => remoteControl?.State;

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
				ExecuteRemoteEdit(() => remoteClient.RenameComponentAsync(remoteDocumentVersion, componentName, newName,
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
		}

		internal void AddRemoteControl(string parentName, string controlType, string componentName, int x, int y)
		{
			if (!IsRemoteDesignerLoaded)
				throw new InvalidOperationException("The out-of-process WinForms designer is not loaded.");
			ExecuteRemoteEdit(() => remoteClient.AddControlAsync(remoteDocumentVersion, parentName, controlType, componentName, x, y,
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
			ExecuteRemoteEdit(() => remoteClient.DeleteComponentAsync(remoteDocumentVersion, componentName,
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
			ExecuteRemoteEdit(() => remoteClient.ApplyLayoutAsync(remoteDocumentVersion, operation, names,
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
			ExecuteRemoteEdit(() => remoteClient.ApplyLayoutAsync(remoteDocumentVersion, "move", names,
				System.Threading.CancellationToken.None, deltaX, deltaY).GetAwaiter().GetResult());
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
			
			if (!FormKeyHandler.inserted) {
				FormKeyHandler.Insert();
			}
			
			this.primaryViewContent = primaryViewContent;
			
			this.UserContent = this.pleaseWaitLabel;
			
			this.sourceCodeStorage = new DesignerSourceCodeStorage();
			this.resourceStore = new ResourceStore(this);
			
			this.IsActiveViewContentChanged += this.IsActiveViewContentChangedHandler;
			
			timer.Tick += Timer_Tick;
			FileService.FileRemoving += this.FileServiceFileRemoving;
			SD.Debugger.DebugStarting += this.DebugStarting;
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
		
		void Timer_Tick(object sender, System.EventArgs e)
		{
			// The WinForms designer internally relies on Application.Idle for some actions, e.g. 'Show Code'
			// This event does not get raised in a WPF application.
			// While we do forward WPF's equivalent idle event to WinForms (see WorkbenchStartup.cs),
			// it doesn't happen often enough -- in particular, it doesn't get raised while the mouse
			// is over the WinForms design surface.
			// This caused the bug: https://github.com/icsharpcode/SharpDevelop/issues/525
			// As a workaround, we use a timer to raise the event while the designer is open.
			// Note: this timer is implemented in the WinForms designer and not globally in SharpDevelop
			// so that we don't wake up the CPU unnecessarily when the designer is not in use.
			Application.RaiseIdle(e);
		}
		
		bool inMasterLoadOperation;
		
		protected override void LoadInternal(OpenedFile file, System.IO.Stream stream)
		{
			LoggingService.Debug("Forms designer: Load " + file.FileName + "; inMasterLoadOperation=" + this.inMasterLoadOperation);
			
			if (this.typeResolutionService != null)
				this.typeResolutionService.ClearCaches();
			
			if (inMasterLoadOperation) {
				
				if (this.sourceCodeStorage.ContainsFile(file)) {
					LoggingService.Debug("Forms designer: Loading " + file.FileName + " in source code storage");
					this.sourceCodeStorage.LoadFile(file, stream);
				} else {
					LoggingService.Debug("Forms designer: Loading " + file.FileName + " in resource store");
					this.resourceStore.Load(file, stream);
				}
				
			} else if (file == this.PrimaryFile || this.sourceCodeStorage.ContainsFile(file)) {
				
				if (this.loader != null && this.loader.Loading) {
					throw new InvalidOperationException("Designer loading a source code file while DesignerLoader is loading and the view is not in a master load operation. This must not happen.");
				}
				
				if (this.designSurface != null) {
					this.UnloadDesigner();
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
					
					this.LoadAndDisplayDesigner();
					
				} finally {
					this.inMasterLoadOperation = false;
				}
				
			} else {
				
				// Loading a resource file
				
				bool mustReload;
				if (this.loader != null && !this.loader.Loading) {
					LoggingService.Debug("Forms designer: Reloading designer because of LoadInternal on resource file");
					this.UnloadDesigner();
					mustReload = true;
					this.inMasterLoadOperation = true;
				} else {
					mustReload = false;
				}
				
				try {
					LoggingService.Debug("Forms designer: Loading " + file.FileName + " in resource store");
					this.resourceStore.Load(file, stream);
					if (mustReload) {
						this.LoadAndDisplayDesigner();
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
		
		void LoadDesigner()
		{
			LoggingService.Info("Form Designer: BEGIN INITIALIZE");
			
			DefaultServiceContainer serviceContainer = new DefaultServiceContainer();
			serviceContainer.AddService(typeof(System.Windows.Forms.Design.IUIService), new UIService());
			serviceContainer.AddService(typeof(System.Drawing.Design.IToolboxService), toolboxService);
			
			serviceContainer.AddService(typeof(IHelpService), new HelpService());
			serviceContainer.AddService(typeof(System.Drawing.Design.IPropertyValueUIService), new PropertyValueUIService());
			
			serviceContainer.AddService(typeof(System.ComponentModel.Design.IResourceService), new DesignerResourceService(this.resourceStore));
			AmbientProperties ambientProperties = new AmbientProperties();
			serviceContainer.AddService(typeof(AmbientProperties), ambientProperties);
			this.typeResolutionService = new TypeResolutionService(this.PrimaryFileName);
			serviceContainer.AddService(typeof(ITypeResolutionService), this.typeResolutionService);
			serviceContainer.AddService(typeof(DesignerOptionService), new SharpDevelopDesignerOptionService());
			serviceContainer.AddService(typeof(ITypeDiscoveryService), new TypeDiscoveryService());
			serviceContainer.AddService(typeof(MemberRelationshipService), new DefaultMemberRelationshipService());
			serviceContainer.AddService(typeof(ProjectResourceService), CreateProjectResourceService());
			
			// Provide the ImageResourceEditor for all Image and Icon properties
			this.addedTypeDescriptionProviders.Add(typeof(Image), TypeDescriptor.AddAttributes(typeof(Image), new EditorAttribute(typeof(ImageResourceEditor), typeof(System.Drawing.Design.UITypeEditor))));
			this.addedTypeDescriptionProviders.Add(typeof(Icon), TypeDescriptor.AddAttributes(typeof(Icon), new EditorAttribute(typeof(ImageResourceEditor), typeof(System.Drawing.Design.UITypeEditor))));
			
//			if (generator.CodeDomProvider != null) {
//				serviceContainer.AddService(typeof(System.CodeDom.Compiler.CodeDomProvider), generator.CodeDomProvider);
//			}
			
			designSurface = CreateDesignSurface(serviceContainer);
			designSurface.Loading += this.DesignerLoading;
			designSurface.Loaded += this.DesignerLoaded;
			designSurface.Flushed += this.DesignerFlushed;
			designSurface.Unloading += this.DesignerUnloading;
			
			serviceContainer.AddService(typeof(System.ComponentModel.Design.IMenuCommandService), new ICSharpCode.FormsDesigner.Services.MenuCommandService(this, designSurface));
			
			this.loader = loaderProvider.CreateLoader(this);
			designSurface.BeginLoad(this.loader);
			
			if (!designSurface.IsLoaded) {
				throw new FormsDesignerLoadException(FormatLoadErrors(designSurface));
			}
			
			undoEngine = new FormsDesignerUndoEngine(Host);
			serviceContainer.AddService(typeof(UndoEngine), undoEngine);
			
			IComponentChangeService componentChangeService = (IComponentChangeService)designSurface.GetService(typeof(IComponentChangeService));
			componentChangeService.ComponentChanged += ComponentChanged;
			componentChangeService.ComponentAdded   += ComponentListChanged;
			componentChangeService.ComponentRemoved += ComponentListChanged;
			componentChangeService.ComponentRename  += ComponentListChanged;
			this.Host.TransactionClosed += TransactionClose;
			
			ISelectionService selectionService = (ISelectionService)designSurface.GetService(typeof(ISelectionService));
			selectionService.SelectionChanged  += SelectionChangedHandler;
			
			if (IsTabOrderMode) { // fixes SD2-1015
				tabOrderMode = false; // let ShowTabOrder call the designer command again
				ShowTabOrder();
			}
			
			UpdatePropertyPad();
			
			hasUnmergedChanges = false;
			timer.Start();
			
			LoggingService.Info("Form Designer: END INITIALIZE");
		}
		
		ProjectResourceService CreateProjectResourceService()
		{
			var project = GetProjectForFile();
			return new ProjectResourceService(project);
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
		
		bool shouldUpdateSelectableObjects = false;
		
		void TransactionClose(object sender, DesignerTransactionCloseEventArgs e)
		{
			if (shouldUpdateSelectableObjects) {
				// update the property pad after the transaction is *really* finished
				// (including updating the selection)
				SD.MainThread.InvokeAsyncAndForget(UpdatePropertyPad);
				shouldUpdateSelectableObjects = false;
			}
		}
		
		void ComponentChanged(object sender, ComponentChangedEventArgs e)
		{
			bool loading = loader != null && loader.Loading;
			LoggingService.Debug("Forms designer: ComponentChanged: " + (e.Component == null ? "<null>" : e.Component.ToString()) + ", Member=" + (e.Member == null ? "<null>" : e.Member.Name) + ", OldValue=" + (e.OldValue == null ? "<null>" : e.OldValue.ToString()) + ", NewValue=" + (e.NewValue == null ? "<null>" : e.NewValue.ToString()) + "; Loading=" + loading + "; Unloading=" + unloading);
			if (!loading && !unloading) {
				MakeDirty();
			}
		}

		void ComponentListChanged(object sender, EventArgs e)
		{
			bool loading = this.loader != null && this.loader.Loading;
			LoggingService.Debug("Forms designer: Component added/removed/renamed, Loading=" + loading + ", Unloading=" + this.unloading);
			if (!loading && !unloading) {
				shouldUpdateSelectableObjects = true;
				this.MakeDirty();
			}
		}

		void UnloadDesigner()
		{
			UnloadRemoteDesigner();
			LoggingService.Debug("FormsDesigner unloading, setting ActiveDesignSurface to null");
			designSurfaceManager.ActiveDesignSurface = null;
			timer.Stop();
			
			bool savedIsDirty = (this.DesignerCodeFile == null) ? false : this.DesignerCodeFile.IsDirty;
			this.UserContent = this.pleaseWaitLabel;
			if (this.DesignerCodeFile != null) {
				this.DesignerCodeFile.IsDirty = savedIsDirty;
			}
			
			if (designSurface != null) {
				designSurface.Loading -= this.DesignerLoading;
				designSurface.Loaded -= this.DesignerLoaded;
				designSurface.Flushed -= this.DesignerFlushed;
				designSurface.Unloading -= this.DesignerUnloading;
				
				IComponentChangeService componentChangeService = designSurface.GetService(typeof(IComponentChangeService)) as IComponentChangeService;
				if (componentChangeService != null) {
					componentChangeService.ComponentChanged -= ComponentChanged;
					componentChangeService.ComponentAdded   -= ComponentListChanged;
					componentChangeService.ComponentRemoved -= ComponentListChanged;
					componentChangeService.ComponentRename  -= ComponentListChanged;
				}
				if (this.Host != null) {
					this.Host.TransactionClosed -= TransactionClose;
				}
				
				ISelectionService selectionService = designSurface.GetService(typeof(ISelectionService)) as ISelectionService;
				if (selectionService != null) {
					selectionService.SelectionChanged -= SelectionChangedHandler;
				}
				
				designSurface.Unloaded += delegate {
					ServiceContainer serviceContainer = designSurface.GetService(typeof(ServiceContainer)) as ServiceContainer;
					if (serviceContainer != null) {
						// Workaround for .NET bug: .NET unregisters the designer host only if no component throws an exception,
						// but then in a finally block assumes that the designer host is already unloaded.
						// Thus we would get the confusing "InvalidOperationException: The container cannot be disposed at design time"
						// when any component throws an exception.
						
						// See http://community.sharpdevelop.net/forums/p/10928/35288.aspx
						// Reproducible with a custom control that has a designer that crashes on unloading
						// e.g. http://www.codeproject.com/KB/toolbars/WinFormsRibbon.aspx
						
						// We work around this problem by unregistering the designer host manually.
						try {
							var services = (Dictionary<Type, object>)typeof(ServiceContainer).InvokeMember(
								"Services",
								BindingFlags.Instance | BindingFlags.GetProperty | BindingFlags.NonPublic,
								null, serviceContainer, null);
							foreach (var pair in services.ToArray()) {
								if (pair.Value is IDesignerHost) {
									serviceContainer.GetType().InvokeMember(
										"RemoveFixedService",
										BindingFlags.Instance | BindingFlags.InvokeMethod | BindingFlags.NonPublic,
										null, serviceContainer, new object[] { pair.Key });
								}
							}
						} catch (Exception ex) {
							LoggingService.Error(ex);
						}
					}
				};
				try {
					designSurface.Dispose();
				} catch (ExceptionCollection exceptions) {
					foreach (Exception ex in exceptions.Exceptions) {
						LoggingService.Error(ex);
					}
				} finally {
					designSurface = null;
				}
			}
			
			this.typeResolutionService = null;
			this.loader = null;
			UpdatePropertyPad();
			
			foreach (KeyValuePair<Type, TypeDescriptionProvider> entry in this.addedTypeDescriptionProviders) {
				TypeDescriptor.RemoveProvider(entry.Value, entry.Key);
			}
			this.addedTypeDescriptionProviders.Clear();
		}

		readonly PropertyContainer propertyContainer = new PropertyContainer();

		public PropertyContainer PropertyContainer {
			get {
				return propertyContainer;
			}
		}

		public void ShowHelp()
		{
			if (Host == null) {
				return;
			}
			
			ISelectionService selectionService = (ISelectionService)Host.GetService(typeof(ISelectionService));
			if (selectionService != null) {
				Control ctl = selectionService.PrimarySelection as Control;
				if (ctl != null) {
					ICSharpCode.SharpDevelop.HelpProvider.ShowHelp(ctl.GetType().FullName);
				}
			}
		}

		void LoadAndDisplayDesigner()
		{
			SD.AnalyticsMonitor.TrackFeature(typeof(FormsDesignerViewContent), "Load");
			try {
				
				if (UseOutOfProcessDesigner)
					LoadRemoteDesigner();
				else
					LoadDesigner();
				
			} catch (Exception e) {
				
				if (e.InnerException is FormsDesignerLoadException) {
					throw new FormsDesignerLoadException(e.InnerException.Message, e);
				} else if (e is FormsDesignerLoadException) {
					throw;
				} else if (designSurface != null && !designSurface.IsLoaded && designSurface.LoadErrors != null) {
					throw new FormsDesignerLoadException(FormatLoadErrors(designSurface), e);
				} else {
					throw;
				}
				
			}
		}

		static bool UseOutOfProcessDesigner => !String.Equals(
			Environment.GetEnvironmentVariable("OPENDEVELOP_WINFORMS_OOP"), "0", StringComparison.Ordinal);

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
			remoteControl.Show(state);
			base.UserContent = remoteControl;
			hasUnmergedChanges = false;
		}

		DesignerDocumentSnapshot CreateRemoteSnapshot(long version)
		{
			var project = GetProjectForFile();
			var snapshot = new DesignerDocumentSnapshot {
				Version = version,
				ProjectFileName = project?.FileName.ToString() ?? "",
				TargetFramework = (project as MSBuildBasedProject)?.GetEvaluatedProperty("TargetFramework") ?? "",
				ProjectAssemblyPath = project?.OutputAssemblyFullPath.ToString() ?? "",
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
			System.Windows.Input.CommandManager.InvalidateRequerySuggested();
		}

		internal new Control UserContent {
			get {
				CustomWindowsFormsHost host = base.UserContent as CustomWindowsFormsHost;
				return host != null ? host.Child : null;
			}
			set {
				CustomWindowsFormsHost host = base.UserContent as CustomWindowsFormsHost;
				if (value == null) {
					base.UserContent = null;
					// LibreWinForms' WindowsFormsHost doesn't expose a public Dispose() the way
					// the real System.Windows.Forms.Integration.WindowsFormsHost does - fall back
					// to IDisposable if it happens to implement it, otherwise let it be GC'd.
					(host as IDisposable)?.Dispose();
					return;
				}
				if (host != null && host.Child == value) {
					return;
				}
				if (host == null) {
					host = SD.WinForms.CreateWindowsFormsHost(this, true);
				}
				host.Child = value;
				base.UserContent = host;
			}
		}

		void DesignerLoading(object sender, EventArgs e)
		{
			LoggingService.Debug("Forms designer: DesignerLoader loading...");
			this.reloadPending = false;
			this.unloading = false;
			this.UserContent = this.pleaseWaitLabel;
		}

		void DesignerUnloading(object sender, EventArgs e)
		{
			LoggingService.Debug("Forms designer: DesignerLoader unloading...");
			this.unloading = true;
			if (!this.disposing) {
				this.UserContent = this.pleaseWaitLabel;
			}
		}

		bool reloadPending;
		bool unloading;

		void DesignerLoaded(object sender, LoadedEventArgs e)
		{
			// This method is called when the designer has loaded.
			LoggingService.Debug("Forms designer: DesignerLoader loaded, HasSucceeded=" + e.HasSucceeded.ToString());
			this.reloadPending = false;
			this.unloading = false;
			
			if (e.HasSucceeded) {
				// Display the designer on the view content
				bool savedIsDirty = this.DesignerCodeFile.IsDirty;
				Control designView = (Control)this.designSurface.View;
				
				designView.BackColor = Color.White;
				designView.RightToLeft = RightToLeft.No;
				// Make sure auto-scaling is based on the correct font.
				// This is required on Vista, I don't know why it works correctly in XP
				designView.Font = System.Windows.Forms.Control.DefaultFont;
				
				this.UserContent = designView;
				LoggingService.Debug("FormsDesigner loaded, setting ActiveDesignSurface to " + this.designSurface.ToString());
				designSurfaceManager.ActiveDesignSurface = this.designSurface;
				this.DesignerCodeFile.IsDirty = savedIsDirty;
				this.UpdatePropertyPad();
			} else {
				// This method can not only be called during initialization,
				// but also when the designer reloads itself because of
				// a language change.
				// When a load error occurs there, we are not somewhere
				// below the Load method which handles load errors.
				// That is why we create an error text box here anyway.
				TextBox errorTextBox = new TextBox() { Multiline=true, ScrollBars=ScrollBars.Both, ReadOnly=true, BackColor=SystemColors.Window, Dock=DockStyle.Fill };
				errorTextBox.Text = String.Concat(this.LoadErrorHeaderText, FormatLoadErrors(designSurface));
				this.UserContent = errorTextBox;
			}
		}

		void DesignerFlushed(object sender, EventArgs e)
		{
			this.resourceStore.CommitAllResourceChanges();
			this.hasUnmergedChanges = false;
		}

		static string FormatLoadErrors(DesignSurface designSurface)
		{
			StringBuilder sb = new StringBuilder();
			foreach(Exception le in designSurface.LoadErrors) {
				sb.AppendLine(le.ToString());
				sb.AppendLine();
			}
			return sb.ToString();
		}

		public virtual void MergeFormChanges()
		{
			SD.AnalyticsMonitor.TrackFeature(typeof(FormsDesignerViewContent), "Save");
			if (IsRemoteDesignerLoaded) {
				MergeRemoteFormChanges();
				return;
			}
			if (this.HasLoadError || this.designSurface == null) {
				LoggingService.Debug("Forms designer: Cannot merge form changes because the designer is not loaded successfully or not loaded at all");
				return;
			} else if (this.DesignerCodeFile == null) {
				throw new InvalidOperationException("Cannot merge form changes without a designer code file.");
			}
			bool isDirty = this.DesignerCodeFile.IsDirty;
			LoggingService.Info("Merging form changes...");
			designSurface.Flush();
			this.resourceStore.CommitAllResourceChanges();
			LoggingService.Info("Finished merging form changes");
			hasUnmergedChanges = false;
			this.DesignerCodeFile.IsDirty = isDirty;
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

		void IsActiveViewContentChangedHandler(object sender, EventArgs e)
		{
			if (this.IsActiveViewContent) {
				
				LoggingService.Debug("FormsDesigner view content activated, setting ActiveDesignSurface to " + ((this.DesignSurface == null) ? "null" : this.DesignSurface.ToString()));
				designSurfaceManager.ActiveDesignSurface = this.DesignSurface;
				
				if (this.DesignSurface != null && this.Host != null) {
					// Reload designer when a referenced assembly has changed
					// (the default Load/Save logic using OpenedFile cannot catch this case)
					if (this.typeResolutionService.ReferencedAssemblyChanged) {
						IDesignerLoaderService loaderService = this.DesignSurface.GetService(typeof(IDesignerLoaderService)) as IDesignerLoaderService;
						if (loaderService != null) {
							if (!this.Host.Loading) {
								LoggingService.Info("Forms designer reloading due to change in referenced assembly");
								this.reloadPending = true;
								if (!loaderService.Reload()) {
									this.reloadPending = false;
									MessageService.ShowMessage("The designer has detected that a referenced assembly has been changed, but the designer loader did not accept the reload command. Please reload the designer manually by closing and reopening this file.");
								}
							} else {
								LoggingService.Debug("Forms designer detected change in referenced assembly, but is in load operation");
							}
						} else {
							MessageService.ShowMessage("The designer has detected that a referenced assembly has been changed, but it cannot reload itself because IDesignerLoaderService is unavailable. Please reload the designer manually by closing and reopening this file.");
						}
					}
				}
				
			} else {
				LoggingService.Debug("FormsDesigner view content deactivated, setting ActiveDesignSurface to null");
				designSurfaceManager.ActiveDesignSurface = null;
			}
		}

		public override void Dispose()
		{
			disposing = true;
			try {
				// base.Dispose() is called first because it may trigger a call
				// to SaveInternal which requires the designer to be loaded.
				base.Dispose();
			} finally {
				SD.Debugger.DebugStarting -= this.DebugStarting;
				FileService.FileRemoving -= this.FileServiceFileRemoving;
				
				this.UnloadDesigner();
				
				this.IsActiveViewContentChanged -= this.IsActiveViewContentChangedHandler;
				
				this.resourceStore.Dispose();
				
				this.UserContent = null;
				this.pleaseWaitLabel.Dispose();
			}
		}

		void SelectionChangedHandler(object sender, EventArgs args)
		{
			UpdatePropertyPadSelection((ISelectionService)sender);
		}

		void UpdatePropertyPadSelection(ISelectionService selectionService)
		{
			ICollection selection = selectionService.GetSelectedComponents();
			object[] selArray = new object[selection.Count];
			selection.CopyTo(selArray, 0);
			propertyContainer.SelectedObjects = selArray;
			System.Windows.Input.CommandManager.InvalidateRequerySuggested();
		}

		protected void UpdatePropertyPad()
		{
			// The modern PropertyContainer (doc/technotes/ilspy.md docking replacement) only
			// tracks SelectedObject(s) - it has no equivalent of the old Host/SelectableObjects
			// pair, which fed a separate "pick any component in this designer" dropdown in the
			// SharpDevelop 4-era property grid toolbar. Forwarding the current selection (below)
			// is what actually drives the property grid; that dropdown is not ported.
			if (Host != null) {
				ISelectionService selectionService = (ISelectionService)Host.GetService(typeof(ISelectionService));
				if (selectionService != null) {
					UpdatePropertyPadSelection(selectionService);
				}
			} else {
				propertyContainer.Clear();
			}
		}

		sealed class RemoteComponentPropertyProxy : ICustomTypeDescriptor
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
			EventDescriptorCollection ICustomTypeDescriptor.GetEvents() => EventDescriptorCollection.Empty;
			EventDescriptorCollection ICustomTypeDescriptor.GetEvents(Attribute[] attributes) => EventDescriptorCollection.Empty;
			PropertyDescriptorCollection ICustomTypeDescriptor.GetProperties() => GetRemotePropertyDescriptors();
			PropertyDescriptorCollection ICustomTypeDescriptor.GetProperties(Attribute[] attributes) => GetRemotePropertyDescriptors();
			object ICustomTypeDescriptor.GetPropertyOwner(PropertyDescriptor pd) => this;

			PropertyDescriptorCollection GetRemotePropertyDescriptors()
			{
				var descriptors = TypeDescriptor.GetProperties(typeof(RemoteComponentPropertyProxy)).Cast<PropertyDescriptor>().ToList();
				var fixedNames = new HashSet<string>(descriptors.Select(item => item.Name), StringComparer.Ordinal);
				foreach (var property in remoteProperties.Where(item => !fixedNames.Contains(item.Name)))
					descriptors.Add(new RemotePropertyDescriptor(owner, name, property));
				foreach (var remoteEvent in remoteEvents)
					descriptors.Add(new RemoteEventPropertyDescriptor(owner, name, remoteEvent));
				return new PropertyDescriptorCollection(descriptors.ToArray(), true);
			}
		}

		sealed class RemoteEventPropertyDescriptor : PropertyDescriptor
		{
			readonly FormsDesignerViewContent owner;
			readonly string componentName;
			readonly DesignerEventInfo remoteEvent;

			public RemoteEventPropertyDescriptor(FormsDesignerViewContent owner, string componentName, DesignerEventInfo remoteEvent)
				: base("Event_" + remoteEvent.Name, new Attribute[] { new CategoryAttribute("Events") })
			{
				this.owner = owner;
				this.componentName = componentName;
				this.remoteEvent = remoteEvent;
			}

			public override string DisplayName => "⚡ " + remoteEvent.Name;
			public override string Description => remoteEvent.HandlerTypeName;
			public override Type ComponentType => typeof(RemoteComponentPropertyProxy);
			public override Type PropertyType => typeof(string);
			public override bool IsReadOnly => false;
			public override bool CanResetValue(object component) => !String.IsNullOrEmpty(remoteEvent.Handler);
			public override object GetValue(object component) => remoteEvent.Handler ?? "";
			public override void ResetValue(object component) => SetValue(component, "");
			public override void SetValue(object component, object value)
			{
				var handler = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
				owner.SetRemoteEvent(componentName, remoteEvent.Name, handler);
				remoteEvent.Handler = handler;
				OnValueChanged(component, EventArgs.Empty);
			}
			public override bool ShouldSerializeValue(object component) => !String.IsNullOrEmpty(remoteEvent.Handler);
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
				if (IsRemoteDesignerLoaded) return remoteUndo.Count > 0;
				if (undoEngine != null) {
					return undoEngine.EnableUndo;
				}
				return false;
			}
		}
		public bool EnableRedo {
			get {
				if (IsRemoteDesignerLoaded) return remoteRedo.Count > 0;
				if (undoEngine != null) {
					return undoEngine.EnableRedo;
				}
				return false;
			}
		}
		public virtual void Undo()
		{
			if (IsRemoteDesignerLoaded && remoteUndo.Count > 0) {
				remoteRedo.Push(CaptureRemoteDocuments());
				RestoreRemoteDocuments(remoteUndo.Pop());
				return;
			}
			if (undoEngine != null) {
				undoEngine.Undo();
			}
		}

		public virtual void Redo()
		{
			if (IsRemoteDesignerLoaded && remoteRedo.Count > 0) {
				remoteUndo.Push(CaptureRemoteDocuments());
				RestoreRemoteDocuments(remoteRedo.Pop());
				return;
			}
			if (undoEngine != null) {
				undoEngine.Redo();
			}
		}
		#endregion

		#region IClipboardHandler implementation
		bool IsMenuCommandEnabled(CommandID commandID)
		{
			if (designSurface == null) {
				return false;
			}
			
			IMenuCommandService menuCommandService = (IMenuCommandService)designSurface.GetService(typeof(IMenuCommandService));
			if (menuCommandService == null) {
				return false;
			}
			
			System.ComponentModel.Design.MenuCommand menuCommand = menuCommandService.FindCommand(commandID);
			if (menuCommand == null) {
				return false;
			}
			
			//int status = menuCommand.OleStatus;
			return menuCommand.Enabled;
		}

		public bool EnableCut {
			get {
				if (IsRemoteDesignerLoaded) return SelectedRemoteComponent()?.Parent?.Length > 0;
				return IsMenuCommandEnabled(StandardCommands.Cut);
			}
		}

		public bool EnableCopy {
			get {
				if (IsRemoteDesignerLoaded) return SelectedRemoteComponent()?.Parent?.Length > 0;
				return IsMenuCommandEnabled(StandardCommands.Copy);
			}
		}

		const string ComponentClipboardFormat = "CF_DESIGNERCOMPONENTS";
		public bool EnablePaste {
			get {
				if (IsRemoteDesignerLoaded) return remoteClipboard?.Count > 0;
				return IsMenuCommandEnabled(StandardCommands.Paste);
			}
		}

		public bool EnableDelete {
			get {
				if (IsRemoteDesignerLoaded) return SelectedRemoteComponent()?.Parent?.Length > 0;
				return IsMenuCommandEnabled(StandardCommands.Delete);
			}
		}

		public bool EnableSelectAll {
			get {
				return IsRemoteDesignerLoaded || designSurface != null;
			}
		}

		public void Cut()
		{
			if (IsRemoteDesignerLoaded) {
				remoteClipboard = SelectedRemoteComponents();
				if (remoteClipboard.Count == 0) return;
				DeleteRemoteComponents(remoteClipboard);
				return;
			}
			IMenuCommandService menuCommandService = (IMenuCommandService)designSurface.GetService(typeof(IMenuCommandService));
			menuCommandService.GlobalInvoke(StandardCommands.Cut);
		}

		public void Copy()
		{
			if (IsRemoteDesignerLoaded) {
				var selected = SelectedRemoteComponents();
				if (selected.Count > 0) remoteClipboard = selected;
				return;
			}
			IMenuCommandService menuCommandService = (IMenuCommandService)designSurface.GetService(typeof(IMenuCommandService));
			menuCommandService.GlobalInvoke(StandardCommands.Copy);
		}

		public void Paste()
		{
			if (IsRemoteDesignerLoaded && remoteClipboard?.Count > 0) {
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
						latest = remoteClient.AddControlAsync(remoteDocumentVersion, parent, component.Type, nameMap[component.Name],
							component.X + 10, component.Y + 10, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
						foreach (var property in component.Properties.Where(CanCopyRemoteProperty))
							latest = remoteClient.SetPropertyAsync(remoteDocumentVersion, nameMap[component.Name], property.Name, property.Value,
								System.Threading.CancellationToken.None).GetAwaiter().GetResult();
					}
					return latest;
				});
				return;
			}
			IMenuCommandService menuCommandService = (IMenuCommandService)designSurface.GetService(typeof(IMenuCommandService));
			menuCommandService.GlobalInvoke(StandardCommands.Paste);
		}

		public void Delete()
		{
			if (IsRemoteDesignerLoaded) {
				DeleteRemoteComponents(SelectedRemoteComponents());
				return;
			}
			IMenuCommandService menuCommandService = (IMenuCommandService)designSurface.GetService(typeof(IMenuCommandService));
			menuCommandService.GlobalInvoke(StandardCommands.Delete);
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
				DesignerSessionState latest = RemoteDesignerState;
				foreach (var component in roots)
					latest = remoteClient.DeleteComponentAsync(remoteDocumentVersion, component.Name,
						System.Threading.CancellationToken.None).GetAwaiter().GetResult();
				return latest;
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
				return;
			}
			IMenuCommandService menuCommandService = (IMenuCommandService)designSurface.GetService(typeof(IMenuCommandService));
			menuCommandService.GlobalInvoke(StandardCommands.SelectAll);
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
			if (!IsTabOrderMode) {
				if (IsRemoteDesignerLoaded) {
					remoteControl.SetTabOrderMode(true);
					tabOrderMode = true;
					return;
				}
				IMenuCommandService menuCommandService = (IMenuCommandService)designSurface.GetService(typeof(IMenuCommandService));
				menuCommandService.GlobalInvoke(StandardCommands.TabOrder);
				tabOrderMode = true;
			}
		}

		public virtual void HideTabOrder()
		{
			if (IsTabOrderMode) {
				if (remoteControl != null) {
					remoteControl.SetTabOrderMode(false);
					tabOrderMode = false;
					return;
				}
				IMenuCommandService menuCommandService = (IMenuCommandService)designSurface.GetService(typeof(IMenuCommandService));
				menuCommandService.GlobalInvoke(StandardCommands.TabOrder);
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
			UnloadDesigner();
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
				this.UnloadDesigner();
				this.sourceCodeStorage.DesignerCodeFile = null;
			}
			
			// When any of our designer code files is deleted,
			// remove the file from the file list so that
			// the primary view is not closed because of this event.
			this.Files.Remove(file);
			this.sourceCodeStorage.RemoveFile(file);
		}

		#region Design surface manager (static)

		static readonly DesignSurfaceManager designSurfaceManager = new DesignSurfaceManager();

		public static DesignSurface CreateDesignSurface(IServiceProvider serviceProvider)
		{
			return designSurfaceManager.CreateDesignSurface(serviceProvider);
		}

		#endregion

		#region Debugger event handling (to prevent designer reload while debugger is starting)

		void DebugStarting(object sender, EventArgs e)
		{
			if (designSurfaceManager.ActiveDesignSurface != this.DesignSurface ||
			    !this.reloadPending)
				return;
			
			// The designer loader does not reload immediately,
			// but only when the Application.Idle event is raised.
			// When the IsActiveViewContentChangedHandler has been called because of the
			// layout change prior to starting the debugger, and it has
			// initiated a reload because of a changed referenced assembly,
			// the reload can interrupt the starting of the debugger.
			// To prevent this, we explicitly raise the Idle event here.
			LoggingService.Debug("Forms designer: DebugStarting raises the Idle event to force pending reload now");
			Cursor oldCursor = Cursor.Current;
			Cursor.Current = Cursors.WaitCursor;
			try {
				Application.RaiseIdle(EventArgs.Empty);
			} finally {
				Cursor.Current = oldCursor;
			}
		}

		#endregion
	}
}
