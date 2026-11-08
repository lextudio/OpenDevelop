// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// DevFlow access to the editor's light bulb, for tests/OpenDevelop.IntegrationTests. It builds the
// same EditorRefactoringContext and EditorActionsProvider the ContextActionsRenderer builds when the
// caret settles (including the provider-visibility filter), so a test exercises the real chain
// except for the popup itself, which is a WPF Popup absent from the UI tree.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Refactoring;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.AvalonEdit.AddIn.ContextActions
{
	[DevFlowUIThread]
	public static class ContextActionsDevFlowActions
	{
		[DevFlowAction("od.editor.bulb-actions", Description = "List what the light bulb offers for an open file at a document offset (the visible actions of every IContextActionProvider on its editor). With executeTitle, also runs the first action with that exact title and reports whether it applied.")]
		public static async Task<string> GetBulbActionsAsync(string path, int offset, string? executeTitle = null)
		{
			try {
				var viewContent = SD.FileService.GetOpenFile(FileName.Create(path)) ?? SD.FileService.OpenFile(FileName.Create(path));
				var editor = viewContent?.GetService<ITextEditor>();
				if (editor == null)
					return JsonSerializer.Serialize(new { success = false, error = "No text editor for " + path });

				var location = editor.Document.GetLocation(Math.Min(offset, editor.Document.TextLength));
				editor.JumpTo(location.Line, location.Column);

				var context = new EditorRefactoringContext(editor);
				var provider = new EditorActionsProvider(context, editor.ContextActionProviders.ToArray());
				var actions = (await provider.GetVisibleActionsAsync(CancellationToken.None)).ToList();

				bool? applied = null;
				if (executeTitle != null) {
					var target = actions.FirstOrDefault(a => a.GetDisplayName(context) == executeTitle);
					if (target is LanguageServiceContextAction languageServiceAction)
						applied = await languageServiceAction.ExecuteAsync(context);
					else if (target != null) {
						target.Execute(context);
						applied = true;
					} else
						applied = false;
				}

				return JsonSerializer.Serialize(new {
					success = true,
					providers = editor.ContextActionProviders.Select(p => p.ID).ToArray(),
					count = actions.Count,
					actions = actions.Select(a => new { title = a.GetDisplayName(context), provider = a.Provider?.ID }).ToArray(),
					applied
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}
	}
}
