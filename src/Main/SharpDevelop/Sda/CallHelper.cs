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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Commands;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.SharpDevelop.Editor.Bookmarks;
using ICSharpCode.SharpDevelop.Logging;
using ICSharpCode.SharpDevelop.Parser;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.SharpDevelop.Sda
{
	internal sealed class CallHelper : MarshalByRefObject
	{
		SharpDevelopHost.CallbackHelper callback;
		bool useSharpDevelopErrorHandler;
		
		[Obsolete]
		public override object InitializeLifetimeService()
		{
			return null;
		}
		
		#region Initialize Core
		public void InitSharpDevelopCore(SharpDevelopHost.CallbackHelper callback, StartupSettings properties)
		{
			// Initialize the most important services:
			var container = new SharpDevelopServiceContainer();
			container.AddFallbackProvider(ServiceSingleton.FallbackServiceProvider);
			// Register the real UI message service once. WorkbenchStartup attaches the WPF
			// Dispatcher and owner after the workbench window has been constructed.
			container.AddService(typeof(IMessageService), new WpfMessageService());
			container.AddService(typeof(ILoggingService), new log4netLoggingService());
			container.AddService(typeof(ITaskListService), new TaskListService());
			ServiceSingleton.ServiceProvider = container;
			
			LoggingService.Info("InitSharpDevelop...");
			this.callback = callback;
			CoreStartup startup = new CoreStartup(properties.ApplicationName);
			if (properties.UseSharpDevelopErrorHandler) {
				this.useSharpDevelopErrorHandler = true;
			}
			string configDirectory = properties.ConfigDirectory;
			string dataDirectory = properties.DataDirectory;
			string propertiesName;
			if (properties.PropertiesName != null) {
				propertiesName = properties.PropertiesName;
			} else {
				propertiesName = properties.ApplicationName + "Properties";
			}
			
			if (properties.ApplicationRootPath != null) {
				FileUtility.ApplicationRootPath = properties.ApplicationRootPath;
			}
			
			if (configDirectory == null)
				configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				                               properties.ApplicationName);
			var propertyService = new PropertyService(
				DirectoryName.Create(configDirectory),
				DirectoryName.Create(dataDirectory ?? Path.Combine(FileUtility.ApplicationRootPath, "data")),
				propertiesName);
			
			startup.StartCoreServices(propertyService);

			// MUST run here: before startup.RunInitialization() below (which runs every addin's
			// Autostart command - Git, Package Management, etc - any of which could touch project
			// loading), but after StartCoreServices so PropertyService (which DotNetSdkService reads)
			// is actually registered. MSBuild resolves and caches its toolset location
			// (MSBuildToolsPath) once, process-wide, the first time ANY
			// Microsoft.Build.Evaluation.ProjectCollection is constructed anywhere in the process -
			// falling back to the currently-running entry assembly's own directory (SharpDevelop's own
			// bin folder) if the environment variable isn't set yet. That resolution is permanent for
			// the process; calling MSBuildInternals.InitializeMSBuildEnvironment() later (e.g. from
			// WorkbenchStartup's preload thread, or Solution.CreateProjectCollection() when a solution
			// opens) no longer has any effect once MSBuild has already cached the wrong path. Multiple
			// call sites construct a ProjectCollection during ordinary startup/solution-load, so
			// reordering just one of them isn't enough - this must run before all of them, here.
			// Symptom when it doesn't: every per-TFM project evaluation fails with "Microsoft.CSharp
			// .targets was not found" at .../SharpDevelop/bin/.../, which starves Roslyn parse info for
			// every file, which is why the class/member dropdown bar above the code editor never
			// appears.
			MSBuildInternals.InitializeMSBuildEnvironment();

			Assembly exe = Assembly.Load(properties.ResourceAssemblyName);
			SD.ResourceService.RegisterNeutralStrings(new ResourceManager("ICSharpCode.SharpDevelop.Resources.StringResources", exe));

			
			CommandWrapper.LinkCommandCreator = (link => new LinkCommand(link));
			CommandWrapper.WellKnownCommandCreator = Core.Presentation.MenuService.GetKnownCommand;
			CommandWrapper.RegisterConditionRequerySuggestedHandler = (eh => CommandManager.RequerySuggested += eh);
			CommandWrapper.UnregisterConditionRequerySuggestedHandler = (eh => CommandManager.RequerySuggested -= eh);
			StringParser.RegisterStringTagProvider(new SharpDevelopStringTagProvider());
			
			// Must be registered before startup.RunInitialization() below: that call runs every
			// addin's Autostart command (e.g. GitAddIn's RegisterEventsCommand), and
			// RegisterEventsCommand.Run() looks this service up via SD.GetService<...>() to
			// register its Project Browser overlay provider. Registering it after
			// RunInitialization() (as the other core services below still are) meant
			// SD.GetService<IProjectBrowserOverlayService>() always returned null during
			// autostart, so the Git overlay provider silently never got registered and Project
			// Browser file status icons never appeared, no matter what GitStatusCache reported.
			if (SD.Services.GetService(typeof(IProjectBrowserOverlayService)) == null) {
				SD.Services.AddService(typeof(IProjectBrowserOverlayService), new ProjectBrowserOverlayService());
			}

			LoggingService.Info("Looking for AddIns...");
			foreach (string file in properties.addInFiles) {
				startup.AddAddInFile(file);
			}
			foreach (string dir in properties.addInDirectories) {
				startup.AddAddInsFromDirectory(dir);
			}
			
			if (properties.AllowAddInConfigurationAndExternalAddIns) {
				startup.ConfigureExternalAddIns(Path.Combine(configDirectory, "AddIns.xml"));
			}
			if (properties.AllowUserAddIns) {
				startup.ConfigureUserAddIns(Path.Combine(configDirectory, "AddInInstallTemp"),
					Path.Combine(configDirectory, "AddIns"));
			}
			
			LoggingService.Info("Loading AddInTree...");
			startup.RunInitialization();

			if (SD.Services.GetService(typeof(IParserService)) == null) {
				SD.Services.AddService(typeof(IParserService), new LanguageServiceParserAdapter());
			}
			if (SD.Services.GetService(typeof(IProjectService)) == null) {
				SD.Services.AddService(typeof(IProjectService), new SDProjectService());
			}
			if (SD.Services.GetService(typeof(IDisplayBindingService)) == null) {
				SD.Services.AddService(typeof(IDisplayBindingService), new DisplayBindingService());
			}
			if (SD.Services.GetService(typeof(IFileService)) == null) {
				SD.Services.AddService(typeof(IFileService), new Workbench.FileService());
			}
			if (SD.Services.GetService(typeof(IBuildService)) == null) {
				SD.Services.AddService(typeof(IBuildService), new BuildService());
			}
			if (SD.Services.GetService(typeof(IBookmarkManager)) == null) {
				SD.Services.AddService(typeof(IBookmarkManager), new BookmarkManager());
			}
			if (SD.Services.GetService(typeof(IUIService)) == null) {
				SD.Services.AddService(typeof(IUIService), new UIService());
			}
			if (SD.Services.GetService(typeof(IShutdownService)) == null) {
				SD.Services.AddService(typeof(IShutdownService), new ShutdownService());
			}
			if (SD.Services.GetService(typeof(IClipboard)) == null) {
				SD.Services.AddService(typeof(IClipboard), new ClipboardWrapper());
			}
			if (SD.Services.GetService(typeof(IProjectBrowserController)) == null) {
				SD.Services.AddService(typeof(IProjectBrowserController), new ProjectBrowserController());
			}

			// AssemblyParserService (real Mono.Cecil-based assembly parsing) is out of MVP scope
			// (MVP policy 1: no Mono.Cecil submodule) - DomPersistencePath is simply not configured here.

			// Register events to marshal back
			SD.BuildService.BuildStarted   += delegate { this.callback.StartBuild(); };
			SD.BuildService.BuildFinished  += delegate { this.callback.EndBuild(); };
			SD.ProjectService.SolutionOpened += delegate { this.callback.SolutionLoaded(); };
			SD.ProjectService.SolutionClosed += delegate { this.callback.SolutionClosed(); };
			FileUtility.FileLoaded += delegate(object sender, FileNameEventArgs e) { this.callback.FileLoaded(e.FileName); };
			FileUtility.FileSaved  += delegate(object sender, FileNameEventArgs e) { this.callback.FileSaved(e.FileName); };
			
			LoggingService.Info("InitSharpDevelop finished");
		}
		#endregion
		
		#region Initialize and run Workbench
		public void RunWorkbench(WorkbenchSettings settings)
		{
			if (settings.RunOnNewThread) {
				Thread t = new Thread(RunWorkbenchInternal);
				t.SetApartmentState(ApartmentState.STA);
				t.Name = "SDmain";
				t.Start(settings);
			} else {
				RunWorkbenchInternal(settings);
			}
		}
		
		[SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
		void RunWorkbenchInternal(object settings)
		{
			WorkbenchSettings wbSettings = (WorkbenchSettings)settings;
			
			WorkbenchStartup wbc = new WorkbenchStartup();
			LoggingService.Info("Initializing workbench...");
			wbc.InitializeWorkbench();
			
			RunWorkbenchInitializedCommands();
			
			LoggingService.Info("Starting workbench...");
			Exception exception = null;
			// finally start the workbench.
			try {
				callback.BeforeRunWorkbench();
				if (Debugger.IsAttached) {
					wbc.Run(wbSettings.InitialFileList);
				} else {
					try {
						wbc.Run(wbSettings.InitialFileList);
					} catch (Exception ex) {
						exception = ex;
					}
				}
			} finally {
				LoggingService.Info("Unloading services...");
				try {
					// see IShutdownService.Shutdown for a description of the shut down procedure
					WorkbenchSingleton.OnWorkbenchUnloaded();
					var propertyService = SD.PropertyService;
					var shutdownService = (ShutdownService)SD.ShutdownService;
					shutdownService.WaitForBackgroundTasks();
					((IDisposable)SD.Services).Dispose(); // dispose all services
					propertyService.Save();
				} catch (Exception ex) {
					LoggingService.Warn("Exception during unloading", ex);
					if (exception == null) {
						exception = ex;
					}
				}
			}
			LoggingService.Info("Finished running workbench.");
			callback.WorkbenchClosed();
			if (exception != null) {
				const string errorText = "Unhandled exception terminated the workbench";
				LoggingService.Fatal(exception);
				if (useSharpDevelopErrorHandler) {
					ExceptionBox.ShowErrorBox(exception, errorText);
				} else {
					throw new RunWorkbenchException(errorText, exception);
				}
			}
		}
		
		void RunWorkbenchInitializedCommands()
		{
			foreach (ICommand command in AddInTree.BuildItems<ICommand>("/SharpDevelop/Workbench/AutostartAfterWorkbenchInitialized", null, false)) {
				try {
					command.Execute(null);
				} catch (Exception ex) {
					// allow startup to continue if some commands fail
					MessageService.ShowException(ex);
				}
			}
		}
		#endregion
		
		public List<Document> OpenDocuments {
			get {
				List<Document> l = new List<Document>();
				SD.MainThread.InvokeIfRequired(() => GetOpenDocuments(l));
				return l;
			}
		}
		void GetOpenDocuments(List<Document> l)
		{
			foreach (IViewContent vc in SD.Workbench.ViewContentCollection) {
				Document d = Document.FromWindow(vc);
				if (d != null) {
					l.Add(d);
				}
			}
		}
		
		/// <summary>
		/// Opens the document with the specified file name.
		/// </summary>
		public Document OpenDocument(string fileName)
		{
			return SD.MainThread.InvokeIfRequired(() => OpenDocumentInternal(fileName));
		}
		
		Document OpenDocumentInternal(string fileName)
		{
			return Document.FromWindow(FileService.OpenFile(fileName));
		}
		
		public void OpenProject(string fileName)
		{
			SD.MainThread.InvokeIfRequired(() => SD.ProjectService.OpenSolutionOrProject(FileName.Create(fileName)));
		}
		
		public bool IsSolutionOrProject(string fileName)
		{
			return SD.ProjectService.IsSolutionOrProjectFile(FileName.Create(fileName));
		}
		
		public bool CloseWorkbench(bool force)
		{
			return SD.MainThread.InvokeIfRequired(() => CloseWorkbenchInternal(force));
		}
		bool CloseWorkbenchInternal(bool force)
		{
			foreach (IWorkbenchWindow window in SD.Workbench.WorkbenchWindowCollection.ToArray()) {
				if (!window.CloseWindow(force))
					return false;
			}
			SD.Workbench.MainWindow.Close();
			return true;
		}
		
		[SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic", Justification = "needs to be run in correct AppDomain")]
		public void KillWorkbench()
		{
			Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
		}
		
		public bool WorkbenchVisible {
			get {
				return SD.MainThread.InvokeIfRequired<bool>(GetWorkbenchVisibleInternal);
			}
			set {
				SD.MainThread.InvokeIfRequired(() => SetWorkbenchVisibleInternal(value));
			}
		}
		bool GetWorkbenchVisibleInternal()
		{
			return SD.Workbench.MainWindow.Visibility == Visibility.Visible;
		}
		void SetWorkbenchVisibleInternal(bool value)
		{
			SD.Workbench.MainWindow.Visibility = value ? Visibility.Visible : Visibility.Hidden;
		}
	}
}
