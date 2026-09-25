using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>
	/// Answers an integration test queues up for the next modal prompt of a given kind.
	/// </summary>
	/// <remarks>
	/// Under <see cref="TestMode"/> no modal prompt is shown (nobody could answer it), so every prompt
	/// falls back to a safe default - which for a file picker or an input box is "cancelled", making
	/// the command behind it untestable. A test can instead enqueue the answer (the
	/// <c>od.test.queue-dialog-answer</c> DevFlow action) before triggering the command; the next
	/// prompt of that kind consumes it. Outside test mode the queue is never consulted, so a real
	/// user always gets the real dialog.
	/// </remarks>
	public static class TestDialogAnswers
	{
		/// <summary>An open-file picker: the answer is the list of chosen paths.</summary>
		public const string Files = "files";
		/// <summary>A folder picker: the answer is the chosen folder.</summary>
		public const string Folder = "folder";
		/// <summary>A text input box: the answer is the entered text.</summary>
		public const string Input = "input";
		/// <summary>A yes/no question: the answer is "yes" or "no".</summary>
		public const string Question = "question";

		static readonly object gate = new object();
		static readonly Dictionary<string, Queue<string[]>> answers = new Dictionary<string, Queue<string[]>>(StringComparer.OrdinalIgnoreCase);

		public static void Enqueue(string kind, params string[] values)
		{
			if (!IsKnownKind(kind))
				throw new ArgumentException("Unknown dialog kind '" + kind + "'.", nameof(kind));
			lock (gate) {
				if (!answers.TryGetValue(kind, out var queue))
					answers[kind] = queue = new Queue<string[]>();
				queue.Enqueue(values ?? new string[0]);
			}
		}

		/// <summary>Takes the next queued answer of <paramref name="kind"/>; false when not in test mode or nothing is queued.</summary>
		public static bool TryDequeue(string kind, out string[] values)
		{
			values = null;
			if (!TestMode.IsActive)
				return false;
			lock (gate) {
				if (!answers.TryGetValue(kind, out var queue) || queue.Count == 0)
					return false;
				values = queue.Dequeue();
				return true;
			}
		}

		public static void Clear()
		{
			lock (gate)
				answers.Clear();
		}

		static bool IsKnownKind(string kind) =>
			string.Equals(kind, Files, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(kind, Folder, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(kind, Input, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(kind, Question, StringComparison.OrdinalIgnoreCase);
	}
}
