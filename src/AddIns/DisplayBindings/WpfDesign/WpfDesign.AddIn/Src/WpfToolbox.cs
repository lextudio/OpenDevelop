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
using System.Linq;
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

		const string WinFormsDataCategory = "Windows Forms Data";
		const string WinFormsComponentsCategory = "Windows Forms Components";
		const string WinFormsPrintingCategory = "Windows Forms Printing";

		// The full standard WinForms toolbox set, in the same categories and order the original
		// SharpDevelop showed - it is literally the catalog shipped as
		// data/options/SharpDevelopControlLibrary.sdcl, which this pad had never used: the pad
		// used to hardcode ten "popular" controls, so MenuStrip/ToolStrip/StatusStrip, the
		// dialogs, Timer/ImageList and the whole Data/Components/Printing groups were simply
		// absent.
		//
		// Entries are TYPE NAMES, not typeof(...), on purpose: this process loads the portable
		// LibreWinForms System.Windows.Forms, which implements a subset (DateTimePicker,
		// MonthCalendar, NotifyIcon, BindingSource, ... are missing there), and several component
		// entries live in optional runtime assemblies (System.IO.Ports, System.Diagnostics.EventLog,
		// ...). A typeof() reference would not compile against the fork; resolving by name lets
		// every entry the running framework actually provides show up and silently skips the rest,
		// so the same list serves both backends.
		//
		// Dragged items are routed through the real System.Drawing.Design.IToolboxService
		// (registered by FormsDesigner into SD.Services - see DesignerViewContent.cs's own doc
		// comment) rather than WpfDesign's CreateComponentTool, since it's WinForms'
		// ParentControlDesigner.OnDragEnter/OnDragDrop that actually creates the component on a
		// WinForms DesignSurface. Each type gets its own System.Drawing.Design.ToolboxItem,
		// created once and registered with the toolbox service up front (AddToolboxItem) - the
		// drop side's DeserializeToolboxItem only accepts items it already knows about.
		static readonly (string Category, string TypeName)[] WinFormsToolboxCatalog = {
			(WinFormsControlsCategory, "System.Windows.Forms.Button"),
			(WinFormsControlsCategory, "System.Windows.Forms.CheckBox"),
			(WinFormsControlsCategory, "System.Windows.Forms.ComboBox"),
			(WinFormsControlsCategory, "System.Windows.Forms.Label"),
			(WinFormsControlsCategory, "System.Windows.Forms.RadioButton"),
			(WinFormsControlsCategory, "System.Windows.Forms.TextBox"),
			(WinFormsControlsCategory, "System.Windows.Forms.CheckedListBox"),
			(WinFormsControlsCategory, "System.Windows.Forms.DateTimePicker"),
			(WinFormsControlsCategory, "System.Windows.Forms.DomainUpDown"),
			(WinFormsControlsCategory, "System.Windows.Forms.FlowLayoutPanel"),
			(WinFormsControlsCategory, "System.Windows.Forms.GroupBox"),
			(WinFormsControlsCategory, "System.Windows.Forms.HScrollBar"),
			(WinFormsControlsCategory, "System.Windows.Forms.LinkLabel"),
			(WinFormsControlsCategory, "System.Windows.Forms.ListBox"),
			(WinFormsControlsCategory, "System.Windows.Forms.ListView"),
			(WinFormsControlsCategory, "System.Windows.Forms.MaskedTextBox"),
			(WinFormsControlsCategory, "System.Windows.Forms.MonthCalendar"),
			(WinFormsControlsCategory, "System.Windows.Forms.NumericUpDown"),
			(WinFormsControlsCategory, "System.Windows.Forms.Panel"),
			(WinFormsControlsCategory, "System.Windows.Forms.PictureBox"),
			(WinFormsControlsCategory, "System.Windows.Forms.ProgressBar"),
			(WinFormsControlsCategory, "System.Windows.Forms.PropertyGrid"),
			(WinFormsControlsCategory, "System.Windows.Forms.RichTextBox"),
			(WinFormsControlsCategory, "System.Windows.Forms.SplitContainer"),
			(WinFormsControlsCategory, "System.Windows.Forms.TabControl"),
			(WinFormsControlsCategory, "System.Windows.Forms.TableLayoutPanel"),
			(WinFormsControlsCategory, "System.Windows.Forms.ToolTip"),
			(WinFormsControlsCategory, "System.Windows.Forms.TrackBar"),
			(WinFormsControlsCategory, "System.Windows.Forms.TreeView"),
			(WinFormsControlsCategory, "System.Windows.Forms.VScrollBar"),
			(WinFormsControlsCategory, "System.Windows.Forms.WebBrowser"),
			(WinFormsControlsCategory, "System.Windows.Forms.ContextMenuStrip"),
			(WinFormsControlsCategory, "System.Windows.Forms.MenuStrip"),
			(WinFormsControlsCategory, "System.Windows.Forms.StatusStrip"),
			(WinFormsControlsCategory, "System.Windows.Forms.ToolStrip"),
			(WinFormsControlsCategory, "System.Windows.Forms.ToolStripContainer"),
			(WinFormsControlsCategory, "System.Windows.Forms.ColorDialog"),
			(WinFormsControlsCategory, "System.Windows.Forms.ErrorProvider"),
			(WinFormsControlsCategory, "System.Windows.Forms.FontDialog"),
			(WinFormsControlsCategory, "System.Windows.Forms.FolderBrowserDialog"),
			(WinFormsControlsCategory, "System.Windows.Forms.ImageList"),
			(WinFormsControlsCategory, "System.Windows.Forms.HelpProvider"),
			(WinFormsControlsCategory, "System.Windows.Forms.OpenFileDialog"),
			(WinFormsControlsCategory, "System.Windows.Forms.SaveFileDialog"),
			(WinFormsControlsCategory, "System.Windows.Forms.Timer"),

			(WinFormsDataCategory, "System.Windows.Forms.BindingNavigator"),
			(WinFormsDataCategory, "System.Windows.Forms.BindingSource"),
			(WinFormsDataCategory, "System.Windows.Forms.DataGridView"),
			(WinFormsDataCategory, "System.Data.DataSet"),
			(WinFormsDataCategory, "System.Data.DataView"),

			(WinFormsComponentsCategory, "System.ComponentModel.BackgroundWorker"),
			(WinFormsComponentsCategory, "System.Diagnostics.EventLog"),
			(WinFormsComponentsCategory, "System.IO.FileSystemWatcher"),
			(WinFormsComponentsCategory, "System.Windows.Forms.NotifyIcon"),
			(WinFormsComponentsCategory, "System.Diagnostics.PerformanceCounter"),
			(WinFormsComponentsCategory, "System.Diagnostics.Process"),
			(WinFormsComponentsCategory, "System.IO.Ports.SerialPort"),

			(WinFormsPrintingCategory, "System.Windows.Forms.PageSetupDialog"),
			(WinFormsPrintingCategory, "System.Windows.Forms.PrintDialog"),
			(WinFormsPrintingCategory, "System.Drawing.Printing.PrintDocument"),
			(WinFormsPrintingCategory, "System.Windows.Forms.PrintPreviewControl"),
			(WinFormsPrintingCategory, "System.Windows.Forms.PrintPreviewDialog"),
		};

		/// <summary>Resolves a catalog entry's type against the WinForms assembly this process
		/// actually loaded (and, for the component entries, the rest of the loaded runtime),
		/// returning NULL when the running framework does not provide it - see
		/// <see cref="WinFormsToolboxCatalog"/> for why absence is expected and not an error.</summary>
		static Type ResolveToolboxType(string typeName)
		{
			try {
				return typeof(System.Windows.Forms.Control).Assembly.GetType(typeName, false)
					?? Type.GetType(typeName, false)
					?? AppDomain.CurrentDomain.GetAssemblies()
						.Select(assembly => {
							try { return assembly.GetType(typeName, false); } catch { return null; }
						})
						.FirstOrDefault(type => type != null);
			} catch (Exception exception) {
				LoggingService.Debug("WpfToolbox.ResolveToolboxType(" + typeName + "): " + exception.Message);
				return null;
			}
		}

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
			foreach (var (category, typeName) in WinFormsToolboxCatalog) {
				var t = ResolveToolboxType(typeName);
				if (t == null)
					continue;
				System.Drawing.Design.ToolboxItem toolboxItem;
				try {
					toolboxItem = new System.Drawing.Design.ToolboxItem(t);
				} catch (Exception exception) {
					// A type the running framework keeps only as an "unsupported" stub (the
					// dotnet/winforms convention LibreWinForms mirrors) can resolve and still
					// refuse to become a toolbox item. Skip it rather than losing the whole list.
					LoggingService.Debug("WpfToolbox: skipping toolbox item " + typeName + ": " + exception.Message);
					continue;
				}
				toolboxService.AddToolboxItem(toolboxItem);
				winFormsItems.Add(new SharedToolboxItem(category, t.Name, WinFormsScope,
					// Real per-control WinForms toolbox icon. This process loads the LibreWinForms
					// System.Windows.Forms (zero manifest resources), so ToolboxItem.Bitmap /
					// ToolboxBitmapAttribute can never supply one here - the icon is read straight
					// out of the installed Microsoft WinForms assembly instead, without loading it
					// (see ICSharpCode.FormsDesigner.Gui.WinFormsToolboxIconProvider).
					icon: WinFormsToolboxIconSource(t),
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

		/// <summary>The control's real WinForms toolbox icon as a WPF ImageSource, or null (the
		/// pad then simply shows no icon, as before) when it cannot be resolved.</summary>
		static System.Windows.Media.ImageSource WinFormsToolboxIconSource(Type controlType)
		{
			try {
				// NOT disposed: the provider caches its bitmaps for the process lifetime.
				var bitmap = ICSharpCode.SharpDevelop.Gui.WinFormsToolboxIconProvider.GetIcon(controlType.FullName);
				if (bitmap == null)
					return null;
				using var stream = new System.IO.MemoryStream();
				bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
				stream.Position = 0;
				var image = new System.Windows.Media.Imaging.BitmapImage();
				image.BeginInit();
				image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
				image.StreamSource = stream;
				image.EndInit();
				image.Freeze();
				return image;
			} catch (Exception exception) {
				LoggingService.Warn("WpfToolbox.WinFormsToolboxIconSource(" + controlType.FullName + "): " + exception.Message);
				return null;
			}
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
