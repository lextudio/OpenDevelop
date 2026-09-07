// Copyright (c) 2026 AlphaSierraPapa for the SharpDevelop Team
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

using System.Composition;
using System.Linq;
using System.Windows;

using ICSharpCode.ILSpy.AssemblyTree;
using ICSharpCode.ILSpy.Properties;
using ICSharpCode.ILSpy.TreeNodes;
using ICSharpCode.ILSpy.Views;

namespace ICSharpCode.ILSpy.Commands
{
	[ExportContextMenuEntry(Header = nameof(Resources.SetTargetFramework), Category = "Debug", Order = 420)]
	[Shared]
	public sealed class SetTargetFrameworkContextMenuEntry : IContextMenuEntry
	{
		readonly AssemblyTreeModel assemblyTreeModel;

		[ImportingConstructor]
		public SetTargetFrameworkContextMenuEntry(AssemblyTreeModel assemblyTreeModel)
		{
			this.assemblyTreeModel = assemblyTreeModel;
		}

		public bool IsEnabled(TextViewContext context) => true;

		public bool IsVisible(TextViewContext context)
			=> context.SelectedTreeNodes?.Length == 1
				&& context.SelectedTreeNodes[0] is AssemblyTreeNode asm
				&& asm.LoadedAssembly.IsLoadedAsValidAssembly;

		public void Execute(TextViewContext context)
		{
			if (context.SelectedTreeNodes?.FirstOrDefault() is not AssemblyTreeNode node)
				return;
			var assembly = node.LoadedAssembly;
			var current = assembly.GetTargetFrameworkIdAsync().GetAwaiter().GetResult();

			var dlg = new SetTargetFrameworkDialog(current) {
				Owner = Application.Current.MainWindow
			};
			if (dlg.ShowDialog() != true)
				return;

			var frameworkName = dlg.Result.Length == 0 ? null : dlg.Result;
			if (frameworkName == assembly.TargetFrameworkIdOverride
				|| (assembly.TargetFrameworkIdOverride == null && frameworkName == current))
				return;

			ApplyOverride(assemblyTreeModel, node, frameworkName);
		}

		internal static void ApplyOverride(AssemblyTreeModel assemblyTreeModel,
			AssemblyTreeNode node, string frameworkName)
		{
			var assembly = node.LoadedAssembly;
			var path = AssemblyTreeModel.GetPathForNode(node);
			assembly.TargetFrameworkIdOverride = frameworkName;
			assembly.AssemblyList.ReloadAssembly(assembly.FileName);
			var restored = assemblyTreeModel.FindNodeByPath(path, returnBestMatch: true);
			if (restored != null)
				assemblyTreeModel.SelectNode(restored);
			assemblyTreeModel.RefreshDecompiledView();
		}
	}

	[ExportContextMenuEntry(Header = nameof(Resources.ResetTargetFramework), Category = "Debug", Order = 421)]
	[Shared]
	public sealed class ResetTargetFrameworkContextMenuEntry : IContextMenuEntry
	{
		readonly AssemblyTreeModel assemblyTreeModel;

		[ImportingConstructor]
		public ResetTargetFrameworkContextMenuEntry(AssemblyTreeModel assemblyTreeModel)
		{
			this.assemblyTreeModel = assemblyTreeModel;
		}

		public bool IsEnabled(TextViewContext context) => true;

		public bool IsVisible(TextViewContext context)
			=> context.SelectedTreeNodes?.Length == 1
				&& context.SelectedTreeNodes[0] is AssemblyTreeNode asm
				&& asm.LoadedAssembly.IsLoadedAsValidAssembly
				&& asm.LoadedAssembly.TargetFrameworkIdOverride != null;

		public void Execute(TextViewContext context)
		{
			if (context.SelectedTreeNodes?.FirstOrDefault() is not AssemblyTreeNode node)
				return;
			SetTargetFrameworkContextMenuEntry.ApplyOverride(assemblyTreeModel, node, null);
		}
	}
}
