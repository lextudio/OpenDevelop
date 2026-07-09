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
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// Creates WPF BitmapSource objects from images in the ResourceService.
	/// </summary>
	public static class PresentationResourceService
	{
		static readonly Dictionary<string, BitmapSource> bitmapCache = new Dictionary<string, BitmapSource>();
		static readonly IReadOnlyDictionary<string, string> pngResourceMap = new Dictionary<string, string> {
			// QuickClassBrowser icons (RoslynSymbolIcons).
			{ "Icons.16x16.Class", "Resources/VS2017/Class/Class_16x.png" },
			{ "Icons.16x16.Interface", "Resources/VS2017/Interface/Interface_16x.png" },
			{ "Icons.16x16.Struct", "Resources/VS2017/Structure/Structure_16x.png" },
			{ "Icons.16x16.Enum", "Resources/VS2017/Enumerator/Enumerator_16x.png" },
			{ "Icons.16x16.Delegate", "Resources/VS2017/Delegate/Delegate_16x.png" },
			{ "Icons.16x16.Method", "Resources/VS2017/Method/Method_16x.png" },
			{ "Icons.16x16.ExtensionMethod", "Resources/VS2017/ExtensionMethod/ExtensionMethod_16x.png" },
			{ "Icons.16x16.Operator", "Resources/VS2017/Operator/Operator_16x.png" },
			{ "Icons.16x16.Property", "Resources/VS2017/Property/Property_16x.png" },
			{ "Icons.16x16.Field", "Resources/VS2017/Field/Field_16x.png" },
			{ "Icons.16x16.Event", "Resources/VS2017/Event/Event_16x.png" },
			{ "Icons.16x16.Parameter", "Resources/VS2017/Parameter/Parameter_16x.png" },
			{ "Icons.16x16.Local", "Resources/VS2017/LocalVariable/LocalVariable_16x.png" },
			{ "Icons.16x16.NameSpace", "Resources/VS2017/Namespace/Namespace_16x.png" },
			{ "Icons.16x16.Indexer", "Resources/VS2017/Indexer/Indexer_16x.png" },
			{ "Icons.16x16.Literal", "Resources/VS2017/Literal/Literal_16x.png" },
			{ "Icons.16x16.Keyword", "Resources/VS2017/IntelliSenseKeyword/IntelliSenseKeyword_16x.png" },

			{ "Icons.16x16.NewDocumentIcon", "Resources/VS2017/NewFile/NewFile_16x.png" },
			{ "Icons.16x16.NewProjectIcon", "Resources/VS2017/CS/CS_ProjectSENode_16x.png" },
			{ "Icons.16x16.SolutionIcon", "Resources/VS2017/SolutionFolderSwitch/SolutionFolderSwitch_16x.png" },
			{ "Icons.16x16.MiscFiles", "Resources/VS2017/Document/Document_16x.png" },
			{ "Icons.16x16.OpenFileIcon", "Resources/VS2017/OpenFile/OpenFile_16x.png" },
			{ "Icons.16x16.OpenProjectIcon", "Resources/VS2017/ProjectFolderOpen/ProjectFolderOpen_16x.png" },
			{ "Icons.16x16.SaveIcon", "Resources/VS2017/Save/Save_16x.png" },
			{ "Icons.16x16.SaveAllIcon", "Resources/VS2017/SaveAll/SaveAll_16x.png" },
			{ "Icons.16x16.CutIcon", "Resources/VS2017/Cut/Cut_16x.png" },
			{ "Icons.16x16.CopyIcon", "Resources/VS2017/Copy/Copy_16x.png" },
			{ "Icons.16x16.PasteIcon", "Resources/VS2017/Paste/Paste_16x.png" },
			{ "Icons.16x16.DeleteIcon", "Resources/VS2017/Remove/Remove_16x.png" },
			{ "Icons.16x16.UndoIcon", "Resources/VS2017/Undo/Undo_16x.png" },
			{ "Icons.16x16.RedoIcon", "Resources/VS2017/Redo/Redo_16x.png" },
			{ "Icons.16x16.BuildCombine", "Resources/VS2017/BuildSolution/BuildSolution_16x.png" },
			{ "Icons.16x16.BuildCurrentSelectedProject", "Resources/VS2017/BuildSelection/BuildSelection_16x.png" },
			{ "Icons.16x16.RunProgramIcon", "Resources/VS2017/Run/Run_16x.png" },
			{ "Icons.16x16.Debug.StartWithoutDebugging", "Resources/VS2017/StartWithoutDebug/StartWithoutDebug_16x.png" },
			{ "Icons.16x16.Debug.Continue", "Resources/VS2017/Run/Run_16x.png" },
			{ "Icons.16x16.Debug.Break", "Resources/VS2017/Pause/Pause_16x.png" },
			{ "Icons.16x16.StopProcess", "Resources/VS2017/Stop/Stop_16x.png" },
			{ "Icons.16x16.Debug.StepOver", "Resources/VS2017/StepOver/StepOver_16x.png" },
			{ "Icons.16x16.Debug.StepInto", "Resources/VS2017/StepIn/StepIn_16x.png" },
			{ "Icons.16x16.Debug.StepOut", "Resources/VS2017/StepOut/StepOut_16x.png" },
			{ "Icons.16x16.NavigateBack", "Resources/VS2017/Backward/Backward_16x.png" },
			{ "Icons.16x16.NavigateForward", "Resources/VS2017/Forward/Forward_16x.png" },
			{ "Icons.16x16.FindIcon", "Resources/VS2017/FindinFiles/FindinFiles_16x.png" },
			{ "Icons.16x16.FindInFiles", "Resources/VS2017/FindinFiles/FindinFiles_16x.png" },
			{ "Icons.16x16.BrowserRefresh", "Resources/VS2017/Refresh/Refresh_16x.png" },
			{ "Icons.16x16.PropertiesIcon", "Resources/VS2017/Property/Property_16x.png" },
			{ "Icons.16x16.NewFolderIcon", "Resources/VS2017/Folder/Folder_16x.png" },
			{ "Icons.16x16.ClosedFolderBitmap", "Resources/VS2017/Folder/Folder_16x.png" },
			{ "Icons.16x16.OpenFolderBitmap", "Resources/VS2017/FolderOpen/FolderOpen_16x.png" },
			{ "Icons.16x16.HelpIcon", "Resources/VS2017/HelpApplication/HelpApplication_16x.png" },
			{ "Icons.16x16.CloseFileIcon", "Resources/VS2017/Close/Close_16x.png" },
			{ "Icons.16x16.CloseAllDocuments", "Resources/VS2017/Close/Close_16x.png" },

			// Solution Explorer file-type icons (SolutionExplorerIconService). Folder names here
			// match the VS2017 Image Library / UnoDevelop's Icons/*.svg base names 1:1.
			{ "Icons.16x16.CSFile", "Resources/VS2017/CSFile/CSFile_16x.png" },
			{ "Icons.16x16.Control", "Resources/VS2017/Control/Control_16x.png" },
			{ "Icons.16x16.JSONFile", "Resources/VS2017/JSONFile/JSONFile_16x.png" },
			{ "Icons.16x16.XMLFile", "Resources/VS2017/XMLFile/XMLFile_16x.png" },
			{ "Icons.16x16.HTMLFile", "Resources/VS2017/HTMLFile/HTMLFile_16x.png" },
			{ "Icons.16x16.StyleSheet", "Resources/VS2017/StyleSheet/StyleSheet_16x.png" },
			{ "Icons.16x16.JSScript", "Resources/VS2017/JSScript/JSScript_16x.png" },
			{ "Icons.16x16.MarkdownFile", "Resources/VS2017/MarkdownFile/MarkdownFile_16x.png" },
			{ "Icons.16x16.SQLFile", "Resources/VS2017/SQLFile/SQLFile_16x.png" },
			{ "Icons.16x16.ResourceSymbols", "Resources/VS2017/ResourceSymbols/ResourceSymbols_16x.png" },
			{ "Icons.16x16.SettingsFile", "Resources/VS2017/SettingsFile/SettingsFile_16x.png" },
			{ "Icons.16x16.TextFile", "Resources/VS2017/TextFile/TextFile_16x.png" },
			{ "Icons.16x16.Image", "Resources/VS2017/Image/Image_16x.png" },
			{ "Icons.16x16.CSRazorFile", "Resources/VS2017/CSRazorFile/CSRazorFile_16x.png" },
			{ "Icons.16x16.ASPXFile", "Resources/VS2017/ASPXFile/ASPXFile_16x.png" },
			{ "Icons.16x16.MasterPage", "Resources/VS2017/MasterPage/MasterPage_16x.png" },
			{ "Icons.16x16.SkinFile", "Resources/VS2017/SkinFile/SkinFile_16x.png" },
			{ "Icons.16x16.Manifest", "Resources/VS2017/Manifest/Manifest_16x.png" },
			{ "Icons.16x16.BinaryFile", "Resources/VS2017/BinaryFile/BinaryFile_16x.png" },
			{ "Icons.16x16.VBFile", "Resources/VS2017/VBFile/VBFile_16x.png" },
			{ "Icons.16x16.FSFile", "Resources/VS2017/FSFile/FSFile_16x.png" },
			{ "Icons.16x16.CPPSourceFile", "Resources/VS2017/CPPSourceFile/CPPSourceFile_16x.png" },
			{ "Icons.16x16.CPPHeaderFile", "Resources/VS2017/CPPHeaderFile/CPPHeaderFile_16x.png" },
			{ "Icons.16x16.CSClassLibrary", "Resources/VS2017/CSClassLibrary/CSClassLibrary_16x.png" },
			{ "Icons.16x16.SolutionFolderSwitch", "Resources/VS2017/SolutionFolderSwitch/SolutionFolderSwitch_16x.png" },

			// Solution Explorer CPS/reference-tree node icons.
			{ "Icons.16x16.Reference", "Resources/VS2017/Reference/Reference_16x.png" },
			{ "Icons.16x16.Library", "Resources/VS2017/Library/Library_16x.png" },
			{ "Icons.16x16.Assembly", "Resources/VS2017/Assembly/Assembly_16x.png" },
			{ "Icons.16x16.Analyzers", "Resources/VS2017/CodeAnalysisWindow/CodeAnalysisWindow_16x.png" },
			{ "Icons.16x16.Frameworks", "Resources/VS2017/MSNETFrameworkDependencies/MSNETFrameworkDependencies_16x.png" },
			{ "Icons.16x16.Application", "Resources/VS2017/Application/Application_16x.png" },
			{ "Icons.16x16.Component", "Resources/VS2017/Component/Component_16x.png" },

			// Output Pad toolbar icons.
			{ "OutputPad.Toolbar.ClearOutputWindow", "Resources/VS2017/ClearWindowContent/ClearWindowContent_16x.png" },
			{ "OutputPad.Toolbar.ToggleWordWrap", "Resources/VS2017/WordWrap/WordWrap_16x.png" },

			// IconBarMargin/Bookmark margin icons (breakpoints, bookmarks).
			// Mapped via "Bookmarks.*" resource name used by BreakpointBookmark / BookmarkBase.
			{ "Bookmarks.Breakpoint", "Resources/VS2017/Breakpoint/Breakpoint_16x.png" },
			{ "Bookmarks.BreakpointConditional", "Resources/VS2017/Breakpoint/BreakpointConditional_16x.png" },
			{ "Bookmarks.DisabledBreakpoint", "Resources/VS2017/Breakpoint/DisabledBreakpoint_16x.png" },
			{ "Bookmarks.UnhealthyBreakpoint", "Resources/VS2017/Breakpoint/UnhealthyBreakpoint_16x.png" },
			{ "Bookmarks.UnhealthyBreakpointConditional", "Resources/VS2017/Breakpoint/UnhealthyBreakpointConditional_16x.png" },
			{ "Bookmarks.ToggleMark", "Resources/VS2017/Bookmark/ToggleMark_16x.png" }
		};
		static readonly IResourceService resourceService;
		
		static PresentationResourceService()
		{
			resourceService = ServiceSingleton.GetRequiredService<IResourceService>();
			resourceService.LanguageChanged += OnLanguageChanged;
		}
		
		static void OnLanguageChanged(object sender, EventArgs e)
		{
			lock (bitmapCache) {
				bitmapCache.Clear();
			}
		}
		
		/// <summary>
		/// Creates a new System.Windows.Controls.Image object containing the image with the
		/// specified resource name.
		/// </summary>
		/// <param name="name">
		/// The name of the requested bitmap.
		/// </param>
		/// <exception cref="ResourceNotFoundException">
		/// Is thrown when the GlobalResource manager can't find a requested resource.
		/// </exception>
		[Obsolete("Use SD.ResourceService.GetImage(name).CreateImage() instead, or just create the image manually")]
		public static System.Windows.Controls.Image GetImage(string name)
		{
			return new System.Windows.Controls.Image {
				Source = GetBitmapSource(name)
			};
		}
		
		/// <summary>
		/// Returns a BitmapSource from the resource database, it handles localization
		/// transparent for the user.
		/// </summary>
		/// <param name="name">
		/// The name of the requested bitmap.
		/// </param>
		/// <returns>The bitmap, or null if no resource is registered under <paramref name="name"/>.</returns>
		public static BitmapSource GetBitmapSource(string name)
		{
			if (resourceService == null)
				throw new ArgumentNullException("resourceService");
			lock (bitmapCache) {
				BitmapSource bs;
				if (bitmapCache.TryGetValue(name, out bs))
					return bs;
				if (TryGetPngBitmapSource(name, out bs)) {
					bitmapCache[name] = bs;
					return bs;
				}
				System.Drawing.Bitmap bmp = (System.Drawing.Bitmap)resourceService.GetImageResource(name);
				if (bmp == null) {
					// Every caller of GetBitmapSource (menu items, toolbar buttons, pad
					// content, dialogs, tree items, task icons) just assigns the result to
					// an Image.Source-like property, where null ("no icon") is always a
					// valid, harmless fallback. The full upstream .addin/resource set
					// references icons for features excluded from MVP scope (see
					// docs/opendevelop.md MVP policy 3), so a missing icon here is expected,
					// not exceptional - it must not abort building menus/toolbars/pads.
					return null;
				}
				IntPtr hBitmap = bmp.GetHbitmap();
				try {
					bs = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero,
					                                           Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					bs.Freeze();
					bitmapCache[name] = bs;
				} finally {
					NativeMethods.DeleteObject(hBitmap);
				}
				return bs;
			}
		}
		
		static bool TryGetPngBitmapSource(string name, out BitmapSource bitmapSource)
		{
			string resourcePath;
			if (!pngResourceMap.TryGetValue(name, out resourcePath)) {
				bitmapSource = null;
				return false;
			}
			
			try {
				var resourceUri = new Uri("pack://application:,,,/ICSharpCode.Core.Presentation;component/" + resourcePath, UriKind.Absolute);
				var resourceInfo = Application.GetResourceStream(resourceUri);
				if (resourceInfo == null || resourceInfo.Stream == null) {
					bitmapSource = null;
					return false;
				}
				
				using (resourceInfo.Stream) {
					var decoder = new PngBitmapDecoder(resourceInfo.Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
					if (decoder.Frames.Count == 0) {
						bitmapSource = null;
						return false;
					}
					
					bitmapSource = decoder.Frames[0];
					bitmapSource.Freeze();
				}
				return true;
			} catch (Exception ex) {
				LoggingService.Warn("Could not load PNG icon '" + name + "' from '" + resourcePath + "'.", ex);
				bitmapSource = null;
				return false;
			}
		}
	}
}
