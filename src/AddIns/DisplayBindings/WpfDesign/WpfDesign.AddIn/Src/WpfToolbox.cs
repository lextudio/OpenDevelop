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
using System.Reflection;
using System.Windows;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.WpfDesign.Designer.Services;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// The WPF + WinForms facade over the merged <see cref="SharedToolbox"/> engine: builds
	/// <see cref="SharedToolboxItem"/>s from WPF's popular-controls set, one group per assembly
	/// referenced by the project being designed, and (once FormsDesigner registers
	/// <c>IToolboxService</c>) a Windows Forms group - then activates the "wpf"+"winforms" scopes
	/// so the one shared ListBox shows exactly those categories. Its own public surface
	/// (<see cref="ISharedToolboxHost.ToolboxControl"/>, <see cref="ToolService"/>,
	/// <see cref="AddProjectDlls"/>) is unchanged from before the merge, so WpfViewContent,
	/// AvalonEditViewContent and WpfDesignDevFlowActions did not need to change.
	/// </summary>
	public class WpfToolbox : ISharedToolboxHost
	{
		const string PopularControlsCategory = "Windows Presentation Foundation";
		const string WinFormsControlsCategory = "Windows Forms";
		const string WpfScope = "wpf";
		const string WinFormsScope = "winforms";

		static WpfToolbox instance;

		public static WpfToolbox Instance {
			get {
				SD.MainThread.VerifyAccess();
				return instance ?? (instance = new WpfToolbox());
			}
		}

		IToolService toolService;

		public WpfToolbox()
		{
			// Guarantees Metadata.GetPopularControls() is populated before this constructor reads
			// it below, regardless of whether a WpfViewContent (which also calls this) has been
			// constructed yet - WpfToolbox.Instance is a lazily-constructed, process-lifetime
			// singleton, and whichever caller touches it first otherwise permanently freezes the
			// popular-controls group as empty if that caller ran before any WpfViewContent existed.
			ICSharpCode.WpfDesign.Designer.BasicMetadata.Register();

			var pointer = new SharedToolboxItem(PopularControlsCategory, "Pointer", WpfScope,
				icon: IconService.GetImageSource("Icons.16x16.FormsDesigner.PointerIcon"),
				onActivated: () => { ClearSelectedWinFormsTool(); SetCurrentTool(null); });
			var popularItems = new List<SharedToolboxItem> { pointer };
			foreach (Type t in Metadata.GetPopularControls())
				popularItems.Add(CreateWpfItem(PopularControlsCategory, t));
			SharedToolbox.Instance.AddItems(popularItems);

			// Registered here (not by FormsDesigner) so neither AddIn needs a compile-time
			// reference to the other - see ISharedToolboxHost's own doc comment.
			SD.Services.AddService(typeof(ISharedToolboxHost), this);
		}

		SharedToolboxItem CreateWpfItem(string categoryName, Type componentType)
		{
			var tool = new CreateComponentTool(componentType);
			return new SharedToolboxItem(categoryName, componentType.Name, WpfScope,
				payload: tool,
				packDragData: data => {
					data.SetData(tool);
					data.SetData(typeof(Type), componentType);
					data.SetData("ComponentTypeName", componentType.FullName);
					// DDP-shaped toolbox payload for WpfSurfaceDesignerControl's drop handler
					// (the out-of-process cutover, see wpf-designer.md): the child resolves types
					// through its own SurfaceTypeFinder.GetType(xamlNamespace, typeName), so this
					// carries a real XAML namespace string rather than a live System.Type.
					data.SetData(typeof(ICSharpCode.SharpDevelop.Designer.Remote.DesignerToolboxItemInfo),
						BuildToolboxItemInfo(componentType));
				},
				onActivated: () => { ClearSelectedWinFormsTool(); SetCurrentTool(tool); });
		}

		// A small representative set, same spirit as Metadata.GetPopularControls() for WPF -
		// dragged items are routed through the real System.Drawing.Design.IToolboxService
		// (registered by FormsDesigner into SD.Services - see DesignerViewContent.cs's own doc
		// comment) rather than WpfDesign's CreateComponentTool, since it's WinForms'
		// ParentControlDesigner.OnDragEnter/OnDragDrop that actually creates the component on a
		// WinForms DesignSurface. Each type gets its own System.Drawing.Design.ToolboxItem,
		// created once and registered with the toolbox service up front (AddToolboxItem) - the
		// drop side's DeserializeToolboxItem only accepts items it already knows about.
		static readonly Type[] WinFormsPopularControls = {
			typeof(System.Windows.Forms.Button),
			typeof(System.Windows.Forms.Label),
			typeof(System.Windows.Forms.TextBox),
			typeof(System.Windows.Forms.CheckBox),
			typeof(System.Windows.Forms.RadioButton),
			typeof(System.Windows.Forms.ComboBox),
			typeof(System.Windows.Forms.ListBox),
			typeof(System.Windows.Forms.Panel),
			typeof(System.Windows.Forms.GroupBox),
			typeof(System.Windows.Forms.NumericUpDown),
		};

		bool winFormsControlsAdded;

		void AddWinFormsControls()
		{
			if (winFormsControlsAdded)
				return;
			var toolboxService = SD.Services.GetService(typeof(System.Drawing.Design.IToolboxService)) as System.Drawing.Design.IToolboxService;
			if (toolboxService == null)
				return;

			winFormsControlsAdded = true;
			var winFormsItems = new List<SharedToolboxItem> {
				new(WinFormsControlsCategory, "Pointer", WinFormsScope,
					icon: IconService.GetImageSource("Icons.16x16.FormsDesigner.PointerIcon"),
					onActivated: () => { ClearSelectedWinFormsTool(); SetCurrentTool(null); })
			};
			foreach (Type t in WinFormsPopularControls) {
				var toolboxItem = new System.Drawing.Design.ToolboxItem(t);
				toolboxService.AddToolboxItem(toolboxItem);
				winFormsItems.Add(new SharedToolboxItem(WinFormsControlsCategory, t.Name, WinFormsScope,
					payload: toolboxItem,
					packDragData: data => data.SetData(typeof(System.Drawing.Design.ToolboxItem), toolboxItem),
					// WPF's own DataObject.SetData(Type, object) and Windows Forms' IDataObject.SetData
					// use the same format-name convention (Type.FullName), and LibreWinForms' portable
					// WindowsFormsHost forwards a WPF drop's data across that boundary format-by-format
					// (CreateFormsDragData) - so a plain WPF DataObject carrying this format is all the
					// WinForms side needs; no OLE marshaling is happening on either side.
					onActivated: () => SetSelectedWinFormsTool(toolboxItem)));
			}
			SharedToolbox.Instance.AddItems(winFormsItems);
		}

		static bool IsControl(Type t)
		{
			return !t.IsAbstract && !t.IsGenericTypeDefinition && t.IsSubclassOf(typeof(FrameworkElement));
		}

		static readonly HashSet<string> addedAssemblies = new HashSet<string>();
		public void AddProjectDlls(OpenedFile file)
		{
			var project = SD.ProjectService.FindProjectContainingFile(file.FileName);
			if (project == null)
				return;

			var typeResolutionService = new TypeResolutionService(file.FileName);

			// Enumerate the project's referenced assemblies from MSBuild's ResolveAssemblyReferences
			// target (the Roslyn-aligned reference source) instead of the old NRefactory
			// ICompilation.ReferencedAssemblies, which is null now that C# projects use Roslyn/LSP.
			foreach (var reference in project.ResolveAssemblyReferences(System.Threading.CancellationToken.None)) {
				string assemblyFileName = reference.FileName;

				if (string.IsNullOrEmpty(assemblyFileName) || !System.IO.File.Exists(assemblyFileName) || addedAssemblies.Contains(assemblyFileName))
					continue;

				try {
					// DO NOT USE Assembly.LoadFrom!!!
					// see http://community.sharpdevelop.net/forums/t/19968.aspx
					Assembly assembly = typeResolutionService.LoadAssembly(assemblyFileName);
					if (assembly == null) continue;

					string categoryName = StringParser.Parse(assembly.FullName.Split(new[] { ',' })[0]);
					var controlTypes = new List<Type>();
					foreach (var t in assembly.GetExportedTypes()) {
						if (IsControl(t))
							controlTypes.Add(t);
					}

					if (controlTypes.Count > 0) {
						var items = new List<SharedToolboxItem>();
						foreach (var t in controlTypes)
							items.Add(CreateWpfItem(categoryName, t));
						SharedToolbox.Instance.AddItems(items);
					}

					addedAssemblies.Add(assemblyFileName);
				} catch (Exception ex) {
					WpfViewContent.DllLoadErrors.Add(new SDTask(new BuildError(assemblyFileName, ex.Message)));
				}
			}
		}

		/// <summary>Maps a real CLR control type to the DDP <c>DesignerToolboxItemInfo</c> shape
		/// (see designer-common.md's Toolbox section) - specifically its
		/// <c>XamlNamespace</c>/<c>TypeName</c> pair, matching the exact string format
		/// <c>XamlTypeFinder.GetXmlNamespaceFor</c>/<c>GetType</c> already use on the child side:
		/// the standard presentation URI for stock <c>System.Windows</c>/<c>System.Windows.Controls</c>
		/// types (the vast majority of toolbox items), falling back to the same
		/// <c>"clr-namespace:X;assembly=Y"</c> form <c>XamlTypeFinder</c> constructs for anything
		/// else - not a fresh convention invented here.</summary>
		internal static ICSharpCode.SharpDevelop.Designer.Remote.DesignerToolboxItemInfo BuildToolboxItemInfo(Type componentType)
		{
			const string presentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
			var isStockPresentationType = componentType.Namespace == "System.Windows.Controls"
				|| componentType.Namespace == "System.Windows.Controls.Primitives"
				|| componentType.Namespace == "System.Windows";
			var xamlNamespace = isStockPresentationType
				? presentationNamespace
				: $"clr-namespace:{componentType.Namespace};assembly={componentType.Assembly.GetName().Name}";
			return new ICSharpCode.SharpDevelop.Designer.Remote.DesignerToolboxItemInfo {
				Name = componentType.Name,
				DisplayName = componentType.Name,
				TypeName = componentType.Name,
				XamlNamespace = xamlNamespace
			};
		}

		static void SetSelectedWinFormsTool(System.Drawing.Design.ToolboxItem item)
		{
			var toolboxService = SD.Services.GetService(typeof(System.Drawing.Design.IToolboxService)) as System.Drawing.Design.IToolboxService;
			toolboxService?.SetSelectedToolboxItem(item);
		}

		static void ClearSelectedWinFormsTool() => SetSelectedWinFormsTool(null);

		void SetCurrentTool(ITool tool) {
			if (toolService != null)
				toolService.CurrentTool = tool ?? toolService.PointerTool;
		}

		public object ToolboxControl {
			get {
				// AddWinFormsControls() no-ops if IToolboxService isn't registered in SD.Services
				// yet - construction order between WpfToolbox and FormsDesigner's static ctor
				// (which registers it) isn't guaranteed, since both are lazily-constructed
				// singletons touched on first use. Retry here (idempotent) so the Windows Forms
				// category still shows up if a .xaml file (which constructs WpfToolbox) happened
				// to be opened before the first WinForms designer file in this session.
				AddWinFormsControls();
				SharedToolbox.Instance.SetActiveScopes(WpfScope, WinFormsScope);
				return SharedToolbox.Instance.ToolboxControl;
			}
		}

		public IToolService ToolService {
			get { return toolService; }
			set {
				if (toolService != null)
					toolService.CurrentToolChanged -= OnCurrentToolChanged;

				toolService = value;

				if (toolService != null) {
					toolService.CurrentToolChanged += OnCurrentToolChanged;
					OnCurrentToolChanged(null, null);
				}
			}
		}

		void OnCurrentToolChanged(object sender, EventArgs e)
		{
			if (toolService == null)
				return;

			var toolToFind = toolService.CurrentTool == toolService.PointerTool ? null : toolService.CurrentTool;
			var item = toolToFind == null ? null : SharedToolbox.Instance.FindByPayload(WpfScope, toolToFind);
			if (item != null)
				SharedToolbox.Instance.Select(item);
			else
				SharedToolbox.Instance.ResetSelection();
		}
	}
}
