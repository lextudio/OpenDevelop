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
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// Creates WPF ImageSource objects from images in the ResourceService.
	/// </summary>
	public static class PresentationResourceService
	{
		static readonly Dictionary<string, ImageSource> imageCache = new Dictionary<string, ImageSource>();
		static readonly IReadOnlyDictionary<string, string> xamlResourceAliases = new Dictionary<string, string> {
			{ "Icons.16x16.Error", "BuildErrorList" },
			{ "Icons.16x16.Warning", "StatusWarning" },
			{ "Icons.16x16.Information", "StatusInformation" },
			{ "Icons.16x16.Question", "HelpApplication" },
			{ "Icons.16x16.Print", "Print" },
			{ "Icons.16x16.PreView", "PrintPreview" },
			{ "Icons.16x16.CancelIcon", "Stop" },
			{ "Icons.16x16.FindNextIcon", "FindNext" },
			{ "Icons.16x16.ReplaceIcon", "FindinFiles" },
			{ "Icons.16x16.CommentRegion", "CommentCode" },
			{ "Icons.16x16.SelectionArrow", "Run" },
			{ "Icons.16x16.DeleteHistory", "ClearWindowContent" },
			{ "Icons.16x16.BrowserBefore", "Backward" },
			{ "Icons.16x16.BrowserAfter", "Forward" },
			{ "Icons.16x16.BrowserCancel", "Stop" },
			{ "Icons.16x16.BrowserHome", "Home" },
			{ "Icons.16x16.BrowserWindow", "Application" },
			{ "Icons.16x16.WebSearchIcon", "FindinFiles" },
			{ "ProjectBrowser.Toolbar.Refresh", "Refresh" },
			{ "ProjectBrowser.SolutionFolder.CreateNew", "NewFolder" },
			{ "ProjectBrowser.CodeBehind", "CSFile" },
			{ "PadIcons.ProjectBrowser", "SolutionFolderSwitch" },
			{ "PadIcons.ClassBrowser", "Class" },
			{ "PadIcons.Toolbar", "Settings" },
			{ "PadIcons.ErrorList", "BuildErrorList" },
			{ "PadIcons.TaskList", "TaskList" },
			{ "PadIcons.Output", "Output" },
			{ "PadIcons.FileBrowser", "FolderOpen" },
			{ "PadIcons.FindResults", "FindinFiles" },
			{ "PadIcons.Bookmarks", "Bookmark" },
			{ "PadIcons.DefinitionView", "Method" },
			{ "PadIcons.BreakPoints", "BreakpointEnable" },
			{ "PadIcons.CallStack", "Thread" },
			{ "PadIcons.LoadedModules", "Library" },
			{ "PadIcons.Threads", "Thread" },
			{ "PadIcons.LocalVariables", "LocalVariable" },
			{ "PadIcons.Watches", "Watch" },
			{ "Bookmarks.GotoPrev", "PreviousBookmark" },
			{ "Bookmarks.GotoNext", "NextBookmark" },
			{ "Bookmarks.PrevBreakpoint", "PreviousBookmark" },
			{ "Bookmarks.NextBreakpoint", "NextBookmark" },
			{ "Bookmarks.EnableDisableAll", "BreakpointDisable" },
			{ "Bookmarks.DisableAllBreakpoints", "BreakpointDisable" },
			{ "Bookmarks.DeleteMark", "ClearBookmark" },
			{ "Bookmarks.DeleteAllMarks", "ClearBookmark" },
			{ "Bookmarks.DeleteAllBreakpoints", "ClearBookmark" },
			{ "Icons.16x16.TextFileIcon", "TextFile" },
			{ "Icons.16x16.XMLFileIcon", "XMLFile" },
			{ "Icons.16x16.HTMLIcon", "HTMLFile" },
			{ "Icons.16x16.CSSIcon", "StyleSheet" },
			{ "Icons.16x16.ResourceFileIcon", "ResourceSymbols" },
			{ "Icons.16x16.StrongNameKeyFileIcon", "Key" },
			{ "Icons.16x16.Debug.Assembly", "Assembly" },
			{ "Icons.16x16.WatchAdd", "Watch" },
			{ "Icons.16x16.WatchDelete", "DeleteWatch" },
			{ "Icons.16x16.WatchesDelete", "ClearWindowContent" },
			{ "C#.FileIcon", "CSFile" },
			{ "C#.ProjectIcon", "CS" },
			{ "VB.FileIcon", "VB" },
			{ "VB.ProjectIcon", "VB" },
			{ "F#.FileIcon", "FS" },
			{ "F#.ProjectIcon", "FS" },
			{ "TextTemplate.ProjectBrowser.File", "TextFile" },
			{ "TypeScript.ProjectBrowser.File", "JSScript" },
			{ "C#.File.MvcRazorFile.Small", "CSRazorFile" },
			{ "VBNet.File.MvcRazorFile.Small", "CSRazorFile" },
			{ "ILSpy", "Assembly" }
		};
		static readonly IReadOnlyDictionary<string, string> xamlResourceMap = new Dictionary<string, string> {
			{ "ProjectBrowser.LinkedFileOverlay", "Resources/VS2017/Shortcut/Shortcut_cyanOverlay_16x.xaml" },
			// QuickClassBrowser icons (RoslynSymbolIcons).
			{ "Icons.16x16.Class", "Resources/VS2017/Class/Class_16x.xaml" },
			{ "Icons.16x16.Interface", "Resources/VS2017/Interface/Interface_16x.xaml" },
			{ "Icons.16x16.Struct", "Resources/VS2017/Structure/Structure_16x.xaml" },
			{ "Icons.16x16.Enum", "Resources/VS2017/Enumerator/Enumerator_16x.xaml" },
			{ "Icons.16x16.Delegate", "Resources/VS2017/Delegate/Delegate_16x.xaml" },
			{ "Icons.16x16.Method", "Resources/VS2017/Method/Method_16x.xaml" },
			{ "Icons.16x16.ExtensionMethod", "Resources/VS2017/ExtensionMethod/ExtensionMethod_16x.xaml" },
			{ "Icons.16x16.Operator", "Resources/VS2017/Operator/Operator_16x.xaml" },
			{ "Icons.16x16.Property", "Resources/VS2017/Property/Property_16x.xaml" },
			{ "Icons.16x16.Field", "Resources/VS2017/Field/Field_16x.xaml" },
			{ "Icons.16x16.Event", "Resources/VS2017/Event/Event_16x.xaml" },
			{ "Icons.16x16.Parameter", "Resources/VS2017/Parameter/Parameter_16x.xaml" },
			{ "Icons.16x16.Local", "Resources/VS2017/LocalVariable/LocalVariable_16x.xaml" },
			{ "Icons.16x16.NameSpace", "Resources/VS2017/Namespace/Namespace_16x.xaml" },
			{ "Icons.16x16.Indexer", "Resources/VS2017/Indexer/Indexer_16x.xaml" },
			{ "Icons.16x16.Literal", "Resources/VS2017/Literal/Literal_16x.xaml" },
			{ "Icons.16x16.Keyword", "Resources/VS2017/IntelliSenseKeyword/IntelliSenseKeyword_16x.xaml" },

			{ "Icons.16x16.NewDocumentIcon", "Resources/VS2017/NewFile/NewFile_16x.xaml" },
			{ "Icons.16x16.NewProjectIcon", "Resources/VS2017/CS/CS_ProjectSENode_16x.xaml" },
			{ "Icons.16x16.SolutionIcon", "Resources/VS2017/SolutionFolderSwitch/SolutionFolderSwitch_16x.xaml" },
			{ "Icons.16x16.MiscFiles", "Resources/VS2017/Document/Document_16x.xaml" },
			{ "Icons.16x16.OpenFileIcon", "Resources/VS2017/OpenFile/OpenFile_16x.xaml" },
			{ "Icons.16x16.OpenProjectIcon", "Resources/VS2017/ProjectFolderOpen/ProjectFolderOpen_16x.xaml" },
			{ "Icons.16x16.SaveIcon", "Resources/VS2017/Save/Save_16x.xaml" },
			{ "Icons.16x16.SaveAllIcon", "Resources/VS2017/SaveAll/SaveAll_16x.xaml" },
			{ "Icons.16x16.CutIcon", "Resources/VS2017/Cut/Cut_16x.xaml" },
			{ "Icons.16x16.CopyIcon", "Resources/VS2017/Copy/Copy_16x.xaml" },
			{ "Icons.16x16.PasteIcon", "Resources/VS2017/Paste/Paste_16x.xaml" },
			{ "Icons.16x16.DeleteIcon", "Resources/VS2017/Remove/Remove_16x.xaml" },
			{ "Icons.16x16.UndoIcon", "Resources/VS2017/Undo/Undo_16x.xaml" },
			{ "Icons.16x16.RedoIcon", "Resources/VS2017/Redo/Redo_16x.xaml" },
			{ "Icons.16x16.BuildCombine", "Resources/VS2017/BuildSolution/BuildSolution_16x.xaml" },
			{ "Icons.16x16.BuildCurrentSelectedProject", "Resources/VS2017/BuildSelection/BuildSelection_16x.xaml" },
			{ "Icons.16x16.RunProgramIcon", "Resources/VS2017/Run/Run_16x.xaml" },
			{ "Icons.16x16.RunAllIcon", "Resources/VS2017/RunTest/RunTest_16x.xaml" },
			{ "Icons.16x16.Debug.StartWithoutDebugging", "Resources/VS2017/StartWithoutDebug/StartWithoutDebug_16x.xaml" },
			{ "Icons.16x16.Debug.Continue", "Resources/VS2017/Run/Run_16x.xaml" },
			{ "Icons.16x16.Debug.Break", "Resources/VS2017/Pause/Pause_16x.xaml" },
			{ "Icons.16x16.Debug.StopProcess", "Resources/VS2017/Stop/Stop_16x.xaml" },
			{ "Icons.16x16.StopProcess", "Resources/VS2017/Stop/Stop_16x.xaml" },
			{ "Icons.16x16.Debug.StepOver", "Resources/VS2017/StepOver/StepOver_16x.xaml" },
			{ "Icons.16x16.Debug.StepInto", "Resources/VS2017/StepIn/StepIn_16x.xaml" },
			{ "Icons.16x16.Debug.StepOut", "Resources/VS2017/StepOut/StepOut_16x.xaml" },
			{ "Icons.16x16.NavigateBack", "Resources/VS2017/Backward/Backward_16x.xaml" },
			{ "Icons.16x16.NavigateForward", "Resources/VS2017/Forward/Forward_16x.xaml" },
			{ "Icons.16x16.FindIcon", "Resources/VS2017/FindinFiles/FindinFiles_16x.xaml" },
			{ "Icons.16x16.FindInFiles", "Resources/VS2017/FindinFiles/FindinFiles_16x.xaml" },
			{ "Icons.16x16.BrowserRefresh", "Resources/VS2017/Refresh/Refresh_16x.xaml" },
			{ "Icons.16x16.PropertiesIcon", "Resources/VS2017/Property/Property_16x.xaml" },
			{ "ProjectBrowser.Toolbar.ShowHiddenFiles", "Resources/VS2017/ShowAllFiles/ShowAllFiles_16x.xaml" },
			{ "Icons.16x16.NewFolderIcon", "Resources/VS2017/Folder/Folder_16x.xaml" },
			{ "Icons.16x16.ClosedFolderBitmap", "Resources/VS2017/Folder/Folder_16x.xaml" },
			{ "Icons.16x16.OpenFolderBitmap", "Resources/VS2017/FolderOpen/FolderOpen_16x.xaml" },
			{ "Icons.16x16.HelpIcon", "Resources/VS2017/HelpApplication/HelpApplication_16x.xaml" },
			{ "Icons.16x16.CloseFileIcon", "Resources/VS2017/Close/Close_16x.xaml" },
			{ "Icons.16x16.CloseAllDocuments", "Resources/VS2017/Close/Close_16x.xaml" },
			{ "Icons.16x16.FullScreen", "Resources/VS2017/ExtendToFullScreen/ExtendToFullScreen_16x.xaml" },
			{ "Icons.16x16.NextWindowIcon", "Resources/VS2017/NewWindow/NewWindow_16x.xaml" },
			{ "Icons.16x16.PrevWindowIcon", "Resources/VS2017/NewWindow/NewWindow_16x.xaml" },
			{ "Icons.16x16.AboutIcon", "Resources/VS2017/UIAboutBox/UIAboutBox_16x.xaml" },
			{ "Icons.16x16.LowerToUpperCase", "Resources/VS2017/TextFile/TextFile_16x.xaml" },
			{ "Icons.16x16.UpperToLowerCase", "Resources/VS2017/TextFile/TextFile_16x.xaml" },
			{ "Icons.16x16.ArrowUp", "Resources/VS2017/PreviousBookmark/PreviousBookmark_16x.xaml" },
			{ "Icons.16x16.ArrowDown", "Resources/VS2017/NextBookmark/NextBookmark_16x.xaml" },
			{ "Icons.16x16.CombineIcon", "Resources/VS2017/SolutionFolderSwitch/SolutionFolderSwitch_16x.xaml" },
			{ "Icons.16x16.Workspace", "Resources/VS2017/SolutionFolderSwitch/SolutionFolderSwitch_16x.xaml" },
			{ "Icons.16x16.AssemblyError", "Resources/VS2017/Assembly/Assembly_16x.xaml" },
			{ "Icons.16x16.AssemblyUnpinned", "Resources/VS2017/Assembly/Assembly_16x.xaml" },

			// Solution Explorer file-type icons (SolutionExplorerIconService). Folder names here
			// match the VS2017 Image Library / UnoDevelop's Icons/*.svg base names 1:1.
			{ "Icons.16x16.CSFile", "Resources/VS2017/CSFile/CSFile_16x.xaml" },
			{ "Icons.16x16.CSSourceFile", "Resources/VS2017/CSSourceFile/CSSourceFile_16x.xaml" },
			{ "Icons.16x16.Control", "Resources/VS2017/Control/Control_16x.xaml" },
			{ "Icons.16x16.JSONFile", "Resources/VS2017/JSONFile/JSONFile_16x.xaml" },
			{ "Icons.16x16.XMLFile", "Resources/VS2017/XMLFile/XMLFile_16x.xaml" },
			{ "Icons.16x16.HTMLFile", "Resources/VS2017/HTMLFile/HTMLFile_16x.xaml" },
			{ "Icons.16x16.StyleSheet", "Resources/VS2017/StyleSheet/StyleSheet_16x.xaml" },
			{ "Icons.16x16.JSScript", "Resources/VS2017/JSScript/JSScript_16x.xaml" },
			{ "Icons.16x16.MarkdownFile", "Resources/VS2017/MarkdownFile/MarkdownFile_16x.xaml" },
			{ "Icons.16x16.SQLFile", "Resources/VS2017/SQLFile/SQLFile_16x.xaml" },
			{ "Icons.16x16.ResourceSymbols", "Resources/VS2017/ResourceSymbols/ResourceSymbols_16x.xaml" },
			{ "Icons.16x16.SettingsFile", "Resources/VS2017/SettingsFile/SettingsFile_16x.xaml" },
			{ "Icons.16x16.TextFile", "Resources/VS2017/TextFile/TextFile_16x.xaml" },
			{ "Icons.16x16.Image", "Resources/VS2017/Image/Image_16x.xaml" },
			{ "Icons.16x16.CSRazorFile", "Resources/VS2017/CSRazorFile/CSRazorFile_16x.xaml" },
			{ "Icons.16x16.ASPXFile", "Resources/VS2017/ASPXFile/ASPXFile_16x.xaml" },
			{ "Icons.16x16.MasterPage", "Resources/VS2017/MasterPage/MasterPage_16x.xaml" },
			{ "Icons.16x16.SkinFile", "Resources/VS2017/SkinFile/SkinFile_16x.xaml" },
			{ "Icons.16x16.Manifest", "Resources/VS2017/Manifest/Manifest_16x.xaml" },
			{ "Icons.16x16.BinaryFile", "Resources/VS2017/BinaryFile/BinaryFile_16x.xaml" },
			{ "Icons.16x16.VBFile", "Resources/VS2017/VB/VB_16x.xaml" },
			{ "Icons.16x16.FSFile", "Resources/VS2017/FS/FS_FileSENode_16x.xaml" },
			{ "Icons.16x16.CPPSourceFile", "Resources/VS2017/CPPSourceFile/CPPSourceFile_16x.xaml" },
			{ "Icons.16x16.CPPHeaderFile", "Resources/VS2017/CPPHeaderFile/CPPHeaderFile_16x.xaml" },
			{ "Icons.16x16.CSClassLibrary", "Resources/VS2017/CSClassLibrary/CSClassLibrary_16x.xaml" },
			{ "Icons.16x16.SolutionFolderSwitch", "Resources/VS2017/SolutionFolderSwitch/SolutionFolderSwitch_16x.xaml" },

			// Solution Explorer CPS/reference-tree node icons.
			{ "Icons.16x16.Reference", "Resources/VS2017/Reference/Reference_16x.xaml" },
			{ "Icons.16x16.Library", "Resources/VS2017/Library/Library_16x.xaml" },
			{ "Icons.16x16.Assembly", "Resources/VS2017/Assembly/Assembly_16x.xaml" },
			{ "Icons.16x16.Analyzers", "Resources/VS2017/CodeAnalysisWindow/CodeAnalysisWindow_16x.xaml" },
			{ "Icons.16x16.Frameworks", "Resources/VS2017/MSNETFrameworkDependencies/MSNETFrameworkDependencies_16x.xaml" },
			{ "Icons.16x16.Application", "Resources/VS2017/Application/Application_16x.xaml" },
			{ "Icons.16x16.Component", "Resources/VS2017/Component/Component_16x.xaml" },

			// Output Pad toolbar icons.
			{ "OutputPad.Toolbar.ClearOutputWindow", "Resources/VS2017/ClearWindowContent/ClearWindowContent_16x.xaml" },
			{ "OutputPad.Toolbar.ToggleWordWrap", "Resources/VS2017/WordWrap/WordWrap_16x.xaml" },

			// Unit Tests Pad toolbar icons.
			{ "Icons.16x16.OpenCollection", "Resources/VS2017/ExpandAll/ExpandAll_16x.xaml" },
			{ "Icons.16x16.Collection", "Resources/VS2017/CollapseAll/CollapseAll_16x.xaml" },
			{ "Icons.16x16.Options", "Resources/VS2017/Settings/Settings_16x.xaml" },
			{ "PadIcons.NUnitTest", "Resources/VS2017/Test/Test_16x.xaml" },

			// Code Coverage Pad icons.
			{ "CodeCoverage.Icons.16x16.Pad", "Resources/VS2017/CodeCoverage/CodeCoverage_16x.xaml" },
			{ "CodeCoverage.Icons.16x16.File", "Resources/VS2017/CodeCoverage/CodeCoverage_16x.xaml" },
			{ "CodeCoverage.Icons.16x16.Run", "Resources/VS2017/RunTest/RunTest_16x.xaml" },

			// Version control context-menu icons.
			{ "Svn.Commit", "Resources/VS2017/Commit/Commit_16x.xaml" },
			{ "Svn.Diff", "Resources/VS2017/Compare/Compare_16x.xaml" },

			// IconBarMargin/Bookmark margin icons (breakpoints, bookmarks).
			// Mapped via "Bookmarks.*" resource name used by BreakpointBookmark / BookmarkBase.
			{ "Bookmarks.Breakpoint", "Resources/VS2017/Breakpoint/BreakpointEnable_16x.xaml" },
			{ "Bookmarks.BreakpointConditional", "Resources/VS2017/Breakpoint/BreakpointEnable_16x.xaml" },
			{ "Bookmarks.DisabledBreakpoint", "Resources/VS2017/Breakpoint/BreakpointDisable_16x.xaml" },
			{ "Bookmarks.UnhealthyBreakpoint", "Resources/VS2017/BreakpointBound/BreakpointBound_16x.xaml" },
			{ "Bookmarks.UnhealthyBreakpointConditional", "Resources/VS2017/BreakpointBound/BreakpointBound_16x.xaml" },
			{ "Bookmarks.CurrentLine", "Resources/VS2017/NextError/NextError_16x.xaml" },
			{ "Bookmarks.PrevBreakpoint", "Resources/VS2017/Bookmark/PreviousBookmark_16x.xaml" },
			{ "Bookmarks.NextBreakpoint", "Resources/VS2017/Bookmark/NextBookmark_16x.xaml" },
			{ "Bookmarks.EnableDisableAll", "Resources/VS2017/Breakpoint/BreakpointDisable_16x.xaml" },
			{ "Bookmarks.DisableAllBreakpoints", "Resources/VS2017/Breakpoint/BreakpointDisable_16x.xaml" },
			{ "Bookmarks.DeleteMark", "Resources/VS2017/Bookmark/ClearBookmark_16x.xaml" },
			{ "Bookmarks.DeleteAllMarks", "Resources/VS2017/Bookmark/ClearBookmark_16x.xaml" },
			{ "Bookmarks.DeleteAllBreakpoints", "Resources/VS2017/Bookmark/ClearBookmark_16x.xaml" },
			{ "Bookmarks.ToggleMark", "Resources/VS2017/Bookmark/Bookmark_16x.xaml" },
			{ "Bookmarks.GotoPrevInFile", "Resources/VS2017/Bookmark/PreviousBookmark_16x.xaml" },
			{ "Bookmarks.GotoNextInFile", "Resources/VS2017/Bookmark/NextBookmark_16x.xaml" },
			{ "Bookmarks.ClearAll", "Resources/VS2017/Bookmark/ClearBookmark_16x.xaml" },
			{ "PadIcons.BreakPoints", "Resources/VS2017/Breakpoint/BreakpointEnable_16x.xaml" }
		};
		static readonly IResourceService resourceService;
		
		static PresentationResourceService()
		{
			resourceService = ServiceSingleton.GetRequiredService<IResourceService>();
			resourceService.LanguageChanged += OnLanguageChanged;
		}
		
		static void OnLanguageChanged(object sender, EventArgs e)
		{
			lock (imageCache) {
				imageCache.Clear();
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
				Source = GetImageSource(name)
			};
		}
		
		/// <summary>
		/// Returns an ImageSource from the resource database, it handles localization
		/// transparent for the user.
		/// </summary>
		/// <param name="name">
		/// The name of the requested bitmap.
		/// </param>
		/// <returns>The image, or null if no resource is registered under <paramref name="name"/>.</returns>
		public static ImageSource GetImageSource(string name)
		{
			if (resourceService == null)
				throw new ArgumentNullException("resourceService");
			lock (imageCache) {
				ImageSource imageSource;
				if (imageCache.TryGetValue(name, out imageSource))
					return imageSource;
				if (TryGetXamlImageSource(name, out imageSource)) {
					imageCache[name] = imageSource;
					return imageSource;
				}
				return null;
			}
		}

		/// <summary>
		/// Compatibility wrapper for old callers. New code should use <see cref="GetImageSource"/>.
		/// </summary>
		public static ImageSource GetBitmapSource(string name)
		{
			return GetImageSource(name);
		}

		static bool TryGetXamlImageSource(string name, out ImageSource imageSource)
		{
			string resourcePath = GetXamlResourcePath(name);
			if (resourcePath == null) {
				imageSource = null;
				return false;
			}
			
			try {
				using (Stream stream = OpenXamlIconStream(resourcePath)) {
					if (stream == null) {
						imageSource = null;
						return false;
					}
					imageSource = LoadXamlImageSource(stream);
				}
				if (imageSource == null)
					return false;
				imageSource.Freeze();
				return true;
			} catch (Exception ex) {
				LoggingService.Warn("Could not load XAML icon '" + name + "' from '" + resourcePath + "'.", ex);
				imageSource = null;
				return false;
			}
		}

		static Stream OpenXamlIconStream(string resourcePath)
		{
			var resourceUri = new Uri("pack://application:,,,/ICSharpCode.Core.Presentation;component/" + resourcePath, UriKind.Absolute);
			var resourceInfo = Application.GetResourceStream(resourceUri);
			return resourceInfo != null ? resourceInfo.Stream : null;
		}

		static string GetXamlResourcePath(string name)
		{
			string resourcePath;
			if (xamlResourceMap.TryGetValue(name, out resourcePath))
				return resourcePath;
			
			string iconName;
			if (xamlResourceAliases.TryGetValue(name, out iconName))
				return GetResourcePathForIconName(iconName);
			
			iconName = GetIconNameFromResourceName(name);
			return string.IsNullOrWhiteSpace(iconName) ? null : GetResourcePathForIconName(iconName);
		}

		static string GetResourcePathForIconName(string iconName)
		{
			if (string.IsNullOrWhiteSpace(iconName))
				return null;
			return "Resources/VS2017/" + iconName + "/" + iconName + "_16x.xaml";
		}

		static string GetIconNameFromResourceName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;
			int lastDot = name.LastIndexOf('.');
			string iconName = lastDot >= 0 ? name.Substring(lastDot + 1) : name;
			return iconName.EndsWith("Icon", StringComparison.Ordinal) && iconName.Length > "Icon".Length
				? iconName.Substring(0, iconName.Length - "Icon".Length)
				: iconName;
		}

		static ImageSource LoadXamlImageSource(Stream stream)
		{
			var root = XamlReader.Load(CreateCleanXamlStream(stream));
			var image = root as System.Windows.Controls.Image;
			if (image != null)
				return image.Source;
			var drawingImage = root as DrawingImage;
			if (drawingImage != null)
				return drawingImage;
			var drawing = root as Drawing;
			if (drawing != null)
				return new DrawingImage(drawing);
			var viewbox = root as Viewbox;
			if (viewbox != null)
				return GetImageSource(viewbox.Child);
			return GetImageSource(root as DependencyObject);
		}

		static Stream CreateCleanXamlStream(Stream stream)
		{
			using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true)) {
				string xaml = reader.ReadToEnd();
				int start = FindXamlStart(xaml);
				if (start > 0)
					xaml = xaml.Substring(start);
				return new MemoryStream(Encoding.UTF8.GetBytes(xaml));
			}
		}

		static int FindXamlStart(string xaml)
		{
			if (string.IsNullOrEmpty(xaml))
				return 0;
			int start = xaml.IndexOf('<');
			return start >= 0 ? start : xaml.Length;
		}

		static ImageSource GetImageSource(DependencyObject element)
		{
			if (element == null)
				return null;
			var shape = element as System.Windows.Shapes.Shape;
			if (shape != null) {
				var drawingBrush = shape.Fill as DrawingBrush;
				if (drawingBrush != null)
					return new DrawingImage(drawingBrush.Drawing);
			}
			var decorator = element as Decorator;
			if (decorator != null)
				return GetImageSource(decorator.Child);
			var panel = element as Panel;
			if (panel != null) {
				foreach (UIElement child in panel.Children) {
					var imageSource = GetImageSource(child);
					if (imageSource != null)
						return imageSource;
				}
			}
			return null;
		}
	}
}
