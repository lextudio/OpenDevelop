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
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

using ICSharpCode.Core;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Parser;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Startup;

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>
	/// Runs workbench initialization.
	/// Is called by ICSharpCode.SharpDevelop.Sda and should not be called manually!
	/// </summary>
	class WorkbenchStartup
	{
		const string workbenchMemento = "WorkbenchMemento";
		const string activeContentState = "Workbench.ActiveContent";
		App app;
		
		public void InitializeWorkbench()
		{
			app = new App();
			// WindowsFormsHost.EnableWindowsFormsInterop() removed - no WinForms interop in this MVP build.
			ComponentDispatcher.ThreadIdle -= ComponentDispatcher_ThreadIdle; // ensure we don't register twice
			ComponentDispatcher.ThreadIdle += ComponentDispatcher_ThreadIdle;
			LayoutConfiguration.LoadLayoutConfiguration();
			SD.Services.AddService(typeof(IMessageLoop), new DispatcherMessageLoop(app.Dispatcher, SynchronizationContext.Current));
			InitializeWorkbench(new WpfWorkbench(), new AvalonDockLayout());
		}
		
		static void InitializeWorkbench(WpfWorkbench workbench, IWorkbenchLayout layout)
		{
			SD.Services.AddService(typeof(IWorkbench), workbench);
			
			UILanguageService.ValidateLanguage();
			
			TaskService.Initialize();
			Project.CustomToolsService.Initialize();
			
			workbench.Initialize();
			workbench.SetMemento(SD.PropertyService.NestedProperties(workbenchMemento));
			workbench.WorkbenchLayout = layout;
			
			// HACK: eagerly load output pad because pad services cannnot be instanciated from background threads
			SD.Services.AddService(typeof(IOutputPad), CompilerMessageView.Instance);
			
			// IDialogMessageService (WinForms IDialogMessageService.DialogOwner wiring) removed -
			// the WinForms message-service implementation is out of MVP scope.

			var applicationStateInfoService = SD.GetService<ApplicationStateInfoService>();
			if (applicationStateInfoService != null) {
				applicationStateInfoService.RegisterStateGetter(activeContentState, delegate { return SD.Workbench.ActiveContent; });
			}
			
			WorkbenchSingleton.OnWorkbenchCreated();
			
			// initialize workbench-dependent services:
			NavigationService.InitializeService();
			
			workbench.ActiveContentChanged += delegate {
				Debug.WriteLine("ActiveContentChanged to " + workbench.ActiveContent);
				LoggingService.Debug("ActiveContentChanged to " + workbench.ActiveContent);
			};
			workbench.ActiveViewContentChanged += delegate {
				Debug.WriteLine("ActiveViewContentChanged to " + workbench.ActiveViewContent);
				LoggingService.Debug("ActiveViewContentChanged to " + workbench.ActiveViewContent);
			};
			workbench.ActiveWorkbenchWindowChanged += delegate {
				Debug.WriteLine("ActiveWorkbenchWindowChanged to " + workbench.ActiveWorkbenchWindow);
				LoggingService.Debug("ActiveWorkbenchWindowChanged to " + workbench.ActiveWorkbenchWindow);
			};
		}
		
		static void ComponentDispatcher_ThreadIdle(object sender, EventArgs e)
		{
			// Application.RaiseIdle (WinForms) removed - no WinForms message loop to pump in this MVP build.
		}
		
		public void Run(IList<string> fileList)
		{
			bool didLoadSolutionOrFile = false;
			
			NavigationService.SuspendLogging();
			
			foreach (string file in fileList) {
				LoggingService.Info("Open file " + file);
				didLoadSolutionOrFile = true;
				try {
					var fullFileName = FileName.Create(Path.GetFullPath(file));
					
					if (SD.ProjectService.IsSolutionOrProjectFile(fullFileName)) {
						SD.ProjectService.OpenSolutionOrProject(fullFileName);
					} else {
						SharpDevelop.FileService.OpenFile(fullFileName);
					}
				} catch (Exception e) {
					MessageService.ShowException(e, "unable to open file " + file);
				}
			}
			
			// load previous solution
			if (!didLoadSolutionOrFile && SD.PropertyService.Get("SharpDevelop.LoadPrevProjectOnStartup", false)) {
				if (SD.FileService.RecentOpen.RecentProjects.Count > 0) {
					try {
						SD.ProjectService.OpenSolutionOrProject(SD.FileService.RecentOpen.RecentProjects[0]);
						didLoadSolutionOrFile = true;
					} catch (Exception ex) {
						MessageService.ShowException(ex);
					}
				}
			}
			
			if (!didLoadSolutionOrFile) {
				foreach (ICommand command in AddInTree.BuildItems<ICommand>("/SharpDevelop/Workbench/AutostartNothingLoaded", null, false)) {
					try {
						command.Execute(null);
					} catch (Exception ex) {
						MessageService.ShowException(ex);
					}
				}
				StartPreloadThread();
			}
			
			NavigationService.ResumeLogging();
			
			((ParserService)SD.ParserService).StartParserThread();
			
			// finally run the workbench window ...
			app.Run(SD.Workbench.MainWindow);
			
			// save the workbench memento in the ide properties
			try {
				SD.PropertyService.SetNestedProperties(workbenchMemento, ((WpfWorkbench)SD.Workbench).CreateMemento());
			} catch (Exception e) {
				MessageService.ShowException(e, "Exception while saving workbench state.");
			}
		}
		
		#region Preload-Thread
		void StartPreloadThread()
		{
			// Wait until UI is responsive and pads are initialized before starting the thread.
			// We don't want to slow down SharpDevelop's startup, we just want to make opening
			// a project more responsive.
			// (and parallelism doesn't really help here; we're mostly waiting for the disk to load the code)
			// So we do our work in the background while the user decides which project to open.
			SD.MainThread.InvokeAsyncAndForget(
				() => new Thread(PreloadThread) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start(),
				DispatcherPriority.ApplicationIdle
			);
		}
		
		void PreloadThread()
		{
			// Pre-load some stuff to make SharpDevelop more responsive once it is started.
			LoggingService.Debug("Preload-Thread started.");

			// Warm up MSBuild. This is a pure JIT/responsiveness optimization (not required for
			// correctness), so failures here must never crash startup - wrapped in try/catch.
			// ToolsVersion "4.0" (and the old 2003 xmlns) is rejected by modern MSBuild ("Available
			// tools versions are \"Current\""), so the dummy project omits it and lets MSBuild pick
			// its own default tools version.
			try {
				string projectCode = @"
<Project DefaultTargets=""Build"">
  <PropertyGroup>
    <Configuration>Debug</Configuration>
    <Platform>AnyCPU</Platform>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=""System"" />
  </ItemGroup>
  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />
</Project>";
				var project = new Microsoft.Build.Evaluation.Project(
					new System.Xml.XmlTextReader(new System.IO.StringReader(projectCode)), null, null,
					new Microsoft.Build.Evaluation.ProjectCollection());
			} catch (Exception ex) {
				LoggingService.Debug("Preload-Thread MSBuild warm-up failed (non-fatal): " + ex.Message);
			}

			// warm up the XSHD loader
			ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition("C#");
			// Note: the real NRefactory.CSharp parser/resolver/RedundantUsingDirectiveIssue warm-up
			// (JIT preloading for responsiveness) was removed - this is a pure performance optimization
			// depending on the real NRefactory.CSharp parser, which is out of MVP scope
			// (ICSharpCode.TypeSystem.Abstractions is type-system-only, no AST/parser).
			// warm up AvalonEdit (must be done on main thread)
			SD.MainThread.InvokeAsyncAndForget(
				delegate {
					object editor;
					SD.EditorControlService.CreateEditor(out editor);
					LoggingService.Debug("Preload-Thread finished.");
				}, DispatcherPriority.ApplicationIdle);
		}
		#endregion
	}
}
