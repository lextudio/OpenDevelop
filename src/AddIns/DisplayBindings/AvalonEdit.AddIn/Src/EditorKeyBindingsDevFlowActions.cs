using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;

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
		[DevFlowAction("od.editor.key-bindings", Description = "The key bindings the active text editor really has (gesture, command, whether one of the editor:... commands of the Keyboard page); pass a command key such as editor:EditingCommands.DeleteNextWord to see only that command's")]
		public static string GetKeyBindings(string commandKey = "")
		{
			var editor = SD.Workbench.ActiveViewContent?.GetService(typeof(ITextEditor)) as ITextEditor;
			var textArea = editor?.GetService(typeof(TextArea)) as TextArea;
			if (textArea == null)
				return JsonSerializer.Serialize(new { success = false, error = "no active text editor" });
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
	}
}
