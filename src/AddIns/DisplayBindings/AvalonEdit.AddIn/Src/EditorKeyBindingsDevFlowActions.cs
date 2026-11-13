using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;

using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.AvalonEdit.AddIn
{
	[DevFlowUIThread]
	public static class EditorKeyBindingsDevFlowActions
	{
		[DevFlowAction("od.editor.key-bindings", Description = "The key bindings a text editor really has (gesture, command, whether one of the editor:... commands of the Keyboard page); pass a command key such as editor:EditingCommands.DeleteNextWord to see only that command's. By default the active editor; pass editorType (part of an editor control's type name, e.g. DecompilerTextEditor) to take the first such editor found in the main window")]
		public static string GetKeyBindings(string commandKey = "", string editorType = "")
		{
			TextArea textArea;
			if (string.IsNullOrEmpty(editorType)) {
				var editor = SD.Workbench.ActiveViewContent?.GetService(typeof(ITextEditor)) as ITextEditor;
				textArea = editor?.GetService(typeof(TextArea)) as TextArea;
			} else {
				textArea = FindTextArea(SD.Workbench.MainWindow, editorType);
			}
			if (textArea == null)
				return JsonSerializer.Serialize(new { success = false, error = "no such text editor" });
			var lists = new[] { textArea.DefaultInputHandler.CaretNavigation.InputBindings, textArea.DefaultInputHandler.Editing.InputBindings };
			var bindings = lists.SelectMany(l => l).OfType<KeyBinding>()
				.Select(b => new {
					gesture = new KeyGestureConverter().ConvertToInvariantString(new KeyGesture(b.Key, b.Modifiers)),
					command = EditorKeyBindings.KeyFor(b.Command) ?? b.Command?.GetType().Name,
					managed = EditorKeyBindings.KeyFor(b.Command) != null
				})
				.Where(b => string.IsNullOrEmpty(commandKey) || b.command == commandKey)
				.ToList();
			return JsonSerializer.Serialize(new { success = true, count = bindings.Count, bindings });
		}

		/// <summary>The text area of the first editor control whose type name contains <paramref name="editorType"/>, in the visual tree below <paramref name="root"/>.</summary>
		static TextArea FindTextArea(System.Windows.DependencyObject root, string editorType)
		{
			if (root == null)
				return null;
			var editor = root as TextEditor;
			if (editor != null && editor.GetType().Name.Contains(editorType))
				return editor.TextArea;
			int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
			for (int i = 0; i < count; i++) {
				var found = FindTextArea(System.Windows.Media.VisualTreeHelper.GetChild(root, i), editorType);
				if (found != null)
					return found;
			}
			return null;
		}
	}
}
