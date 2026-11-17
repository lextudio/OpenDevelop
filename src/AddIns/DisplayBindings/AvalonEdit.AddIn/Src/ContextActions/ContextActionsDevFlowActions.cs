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

using System.Windows.Input;
using ICSharpCode.AvalonEdit;
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
		[DevFlowAction("od.editor.context-menu", Description = "Build the editor's real right-click menu (the AddIn-tree path CodeEditor.TextAreaContextMenuOpening shows, with the ITextEditor adapter as owner) for an open file with the caret at line/column, and list its item headers. With invoke set, also clicks the first item whose header (access keys removed) equals it - the same path a user's click takes - and reports whether it was found. The popup itself is a separate window, so it is built but not shown.")]
		public static string ContextMenu(string path, int line, int column, string invoke = "", int selectLength = 0)
		{
			try {
				var viewContent = SD.FileService.GetOpenFile(FileName.Create(path)) ?? SD.FileService.OpenFile(FileName.Create(path));
				var editor = viewContent?.GetService<ITextEditor>();
				if (editor == null)
					return JsonSerializer.Serialize(new { success = false, error = "No text editor for " + path });

				// line <= 0 leaves the caret where it is (to read where a command moved it).
				if (line > 0) {
					editor.JumpTo(line, column);
					if (selectLength > 0)
						editor.Select(editor.Caret.Offset, selectLength);
				}
				// Same calls as MenuService.ShowContextMenu, minus IsOpen = true (the popup is a separate window).
				var menu = new System.Windows.Controls.ContextMenu();
				menu.ItemsSource = ICSharpCode.Core.Presentation.MenuService.CreateMenuItems(menu, editor, "/SharpDevelop/ViewContent/AvalonEdit/ContextMenu", "ContextMenu");
				var items = new List<System.Windows.Controls.MenuItem>();
				void Collect(System.Collections.IEnumerable source)
				{
					foreach (var item in source) {
						if (item is System.Windows.Controls.MenuItem menuItem && menuItem.Visibility == System.Windows.Visibility.Visible) {
							items.Add(menuItem);
							Collect(menuItem.Items);
						}
					}
				}
				Collect(menu.Items);
				static string Plain(System.Windows.Controls.MenuItem item) => (item.Header as string ?? "").Replace("_", "");

				bool invoked = false, found = false, enabled = false;
				string commandDiagnostics = null;
				int lengthBefore = editor.Document.TextLength;
				if (!string.IsNullOrEmpty(invoke)) {
					var target = items.FirstOrDefault(i => Plain(i) == invoke);
					found = target != null;
					enabled = target != null && target.IsEnabled;
					if (target != null && target.IsEnabled) {
						// MenuItem.OnClick is what a mouse click ends in: it raises Click and executes the item's Command.
						typeof(System.Windows.Controls.MenuItem).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(target, null);
						invoked = true;
						commandDiagnostics = $"canExecute={target.Command?.CanExecute(target.CommandParameter)}, parameter={target.CommandParameter?.GetType().Name}, command={target.Command?.GetType().Name}, unwrapped={(target.Command is ICSharpCode.Core.CommandWrapper w ? ICSharpCode.Core.CommandWrapper.Unwrap(w)?.GetType().FullName : null)}, formatting={editor.Language?.FormattingStrategy?.GetType().FullName}, selStart={editor.SelectionStart}, selLen={editor.SelectionLength}";
					}
				}
				return JsonSerializer.Serialize(new {
					success = true,
					caretLine = editor.Caret.Line,
					caretColumn = editor.Caret.Column,
					headers = items.Select(Plain).Where(h => h.Length > 0).ToArray(),
					topLevelCount = menu.Items.Count,
					rawItems = menu.Items.Cast<object>().Select(o => (o?.GetType().Name ?? "null") + ":" + ((o as System.Windows.Controls.HeaderedItemsControl)?.Header?.ToString() ?? "")).ToArray(),
					invoked, found, enabled, commandDiagnostics, lengthBefore, lengthAfter = editor.Document.TextLength,
					// FindReferencesCommand reads the ACTIVE view's ITextEditor; report what that is.
					activeView = SD.Workbench.ActiveViewContent?.GetType().FullName,
					activeFile = SD.Workbench.ActiveViewContent?.PrimaryFileName?.ToString(),
					documentText = editor.Document.Text,
					selectionLength = editor.SelectionLength,
					language = editor.Language?.GetType().FullName,
					activeViewHasTextEditor = SD.GetActiveViewContentService<ITextEditor>() != null
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.editor.hierarchy", Description = "Run the query behind the editor's Find Base Classes (kind=base) or Find Derived Classes (kind=derived) menu command for an open file with the caret at line/column, without opening its popup. useOwner=true passes the file's editor as the command's owner (the context-menu path); false passes none, so the command falls back to the ACTIVE view's editor, as before the owner fix.")]
		public static async Task<string> Hierarchy(string path, int line, int column, string kind, bool useOwner)
		{
			try {
				var viewContent = SD.FileService.GetOpenFile(FileName.Create(path)) ?? SD.FileService.OpenFile(FileName.Create(path));
				var editor = viewContent?.GetService<ITextEditor>();
				if (editor == null)
					return JsonSerializer.Serialize(new { success = false, error = "No text editor for " + path });
				editor.JumpTo(line, column);
				var owner = useOwner ? editor : null;
				var result = kind == "derived" ? await FindDerivedClassesOrOverrides.QueryAsync(owner) : await FindBaseClassesOrMembers.QueryAsync(owner);
				return JsonSerializer.Serialize(new {
					success = true,
					found = result != null,
					subject = result?.Subject,
					nodes = result?.Nodes.Select(n => n.Name).ToArray(),
					activeViewHasTextEditor = SD.GetActiveViewContentService<ITextEditor>() != null
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.ToString() });
			}
		}

		[DevFlowAction("od.editor.bulb-popup", Description = "Open the real light-bulb popup for an open file at a document offset, exactly as ContextActionsRenderer does when the caret settles (focus the text area, build the view model, OpenAtLineStart), and report whether it opened. Closes it again.")]
		public static async Task<string> OpenBulbPopupAsync(string path, int offset)
		{
			try {
				var viewContent = SD.FileService.GetOpenFile(FileName.Create(path)) ?? SD.FileService.OpenFile(FileName.Create(path));
				var editor = viewContent?.GetService<ITextEditor>();
				var textArea = (editor?.GetService(typeof(TextEditor)) as TextEditor)?.TextArea;
				if (editor == null || textArea == null)
					return JsonSerializer.Serialize(new { success = false, error = "No text editor for " + path });

				var location = editor.Document.GetLocation(Math.Min(offset, editor.Document.TextLength));
				editor.JumpTo(location.Line, location.Column);
				// The popup only opens while its parent has keyboard focus, which is when the renderer opens it.
				Keyboard.Focus(textArea);

				var actionsProvider = new EditorActionsProvider(new EditorRefactoringContext(editor), editor.ContextActionProviders.ToArray());
				var viewModel = new ContextActionsBulbViewModel(actionsProvider);
				await viewModel.LoadActionsAsync(CancellationToken.None);

				var popup = new ContextActionsBulbPopup(textArea) { ViewModel = viewModel };
				try {
					popup.OpenAtLineStart(editor);
					// Where the popup was placed and where the text area is, both in screen device-independent units (the
					// popup is a separate window and absent from the UI tree, so its position is read from the popup).
					var source = System.Windows.PresentationSource.FromVisual(textArea);
					var toUnits = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
					var topLeft = toUnits.Transform(textArea.PointToScreen(new System.Windows.Point(0, 0)));
					var caretLine = editor.Caret.Line;
					return JsonSerializer.Serialize(new {
						success = true,
						parentFocused = textArea.IsKeyboardFocused,
						isOpen = popup.IsOpen,
						actionCount = viewModel.Actions.Count,
						placement = popup.Placement.ToString(),
						popupX = popup.HorizontalOffset,
						popupY = popup.VerticalOffset,
						textAreaX = topLeft.X,
						textAreaY = topLeft.Y,
						textAreaWidth = textArea.ActualWidth,
						textAreaHeight = textArea.ActualHeight,
						caretLine
					});
				} finally {
					popup.Close();
				}
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.GetType().Name + ": " + ex.Message });
			}
		}

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
