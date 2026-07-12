// DevFlow action used by tests/OpenDevelop.IntegrationTests to inspect the LSP-backed XAML
// outline (XamlOutlineContentHost/XamlOutlineLspProvider) for the active .xaml text editor view
// - i.e. the code-editor Outline pad, not the WPF designer's (unrelated) visual-tree outline.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;
using LeXtudio.DevFlow.Agent.Core;

namespace ICSharpCode.XamlBinding
{
	[DevFlowUIThread]
	public static class XamlOutlineDevFlowActions
	{
		[DevFlowAction("od.xaml-outline.status", Description = "Inspect the LSP-backed Outline pad content for the active .xaml text editor view")]
		public static async Task<string> GetXamlOutlineStatus()
		{
			var viewContent = SD.Workbench.ActiveViewContent;
			var editor = viewContent?.GetService(typeof(ITextEditor)) as ITextEditor;
			if (editor == null || editor.FileName == null ||
			    !string.Equals(Path.GetExtension(editor.FileName), ".xaml", StringComparison.OrdinalIgnoreCase))
				return JsonSerializer.Serialize(new { active = false });

			IReadOnlyList<DocumentOutlineNode> nodes;
			try {
				nodes = await XamlOutlineLspProvider.GetOutlineAsync(editor, CancellationToken.None).ConfigureAwait(true);
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { active = false, error = ex.Message });
			}

			var names = new List<string>();
			foreach (var node in nodes)
				CollectNames(node, names);

			return JsonSerializer.Serialize(new {
				active = true,
				rootName = Path.GetFileName(editor.FileName.ToString()),
				rootChildCount = nodes.Count,
				outlineNames = names.ToArray()
			});
		}

		static void CollectNames(DocumentOutlineNode node, List<string> names)
		{
			names.Add(node.Name);
			foreach (var child in node.Children)
				CollectNames(child, names);
		}
	}
}
