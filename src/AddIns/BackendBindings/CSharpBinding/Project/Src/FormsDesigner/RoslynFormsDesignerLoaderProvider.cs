// A Roslyn-based replacement for the excluded (NRefactory-based) CSharpDesignerLoaderProvider -
// see doc/technotes/csharp-vb-binding.md's "Phase 5: Deferred items" note on the WinForms
// Designer, and CSharpBinding.csproj's own comment on why FormsDesigner/*.cs is excluded there.

using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.IO;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.FormsDesigner;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

namespace CSharpBinding.FormsDesigner
{
	public class RoslynFormsDesignerLoaderProvider : IDesignerLoaderProvider
	{
		public DesignerLoader CreateLoader(FormsDesignerViewContent viewContent)
		{
			return new RoslynDesignerLoader(viewContent);
		}

		/// <summary>
		/// The classic WinForms convention: "Foo.cs" designs alongside a co-located
		/// "Foo.Designer.cs" holding the generated field declarations and InitializeComponent -
		/// InitializeComponent lives in whichever of the two actually declares it (checked by
		/// RoslynDesignerLoader.FindDesignerClass, not assumed here).
		/// </summary>
		public IReadOnlyList<OpenedFile> GetSourceFiles(FormsDesignerViewContent viewContent, out OpenedFile designerCodeFile)
		{
			var primaryFileName = viewContent.PrimaryFileName;
			var files = new List<OpenedFile> { viewContent.PrimaryFile };

			var designerPath = FileName.Create(Path.Combine(
				Path.GetDirectoryName(primaryFileName) ?? "",
				Path.GetFileNameWithoutExtension(primaryFileName) + ".Designer.cs"));

			OpenedFile designerFile = null;
			if (File.Exists(designerPath)) {
				designerFile = SD.FileService.GetOrCreateOpenedFile(designerPath);
				files.Add(designerFile);
			}

			designerCodeFile = designerFile ?? viewContent.PrimaryFile;
			return files;
		}
	}
}
