#nullable enable
// Lets an integration test answer the modal prompts OD_TEST_MODE suppresses (file/folder pickers,
// input boxes, yes/no questions) - see TestDialogAnswers in the Base project.

using System;
using System.Text.Json;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.SharpDevelop.DevFlow
{
	[DevFlowUIThread]
	public static class TestDialogDevFlowActions
	{
		[DevFlowAction("od.test.queue-dialog-answer", Description = "Queue the answer for the next suppressed prompt of a kind (OD_TEST_MODE only): kind is files, folder, input or question; values is a JSON array of strings - file paths for files, one path for folder, the text for input, yes/no for question")]
		public static string QueueDialogAnswer(string kind, string values)
		{
			try {
				if (!TestMode.IsActive)
					return JsonSerializer.Serialize(new { success = false, error = "Only available when the app runs with OD_TEST_MODE=1." });
				var parsed = JsonSerializer.Deserialize<string[]>(values) ?? Array.Empty<string>();
				TestDialogAnswers.Enqueue(kind, parsed);
				return JsonSerializer.Serialize(new { success = true, kind, values = parsed });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = ex.Message });
			}
		}

		[DevFlowAction("od.test.clear-dialog-answers", Description = "Drop every queued prompt answer (OD_TEST_MODE only)")]
		public static string ClearDialogAnswers()
		{
			TestDialogAnswers.Clear();
			return JsonSerializer.Serialize(new { success = true });
		}
	}
}
