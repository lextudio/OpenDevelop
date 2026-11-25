// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the
// Software without restriction, including without limitation the rights to use, copy,
// modify, merge, publish, distribute, sublicense, and/or sell copies of the Software,
// and to permit persons to whom the Software is furnished to do so, subject to the
// following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies
// or substantial portions of the Software.

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace Debugger.AddIn.Service
{
	readonly struct ActiveStatement
	{
		public ActiveStatement(FileName fileName, int startLine, int endLine)
		{
			FileName = fileName;
			StartLine = startLine;
			EndLine = endLine;
		}

		public FileName FileName { get; }
		public int StartLine { get; }
		public int EndLine { get; }
	}

	/// <summary>
	/// Makes the statements the debugger is stopped on read-only, matching Visual Studio's
	/// active-statement rule. With a code-delta Hot Reload session the adapter returns the whole
	/// stack, so callers are protected in their own editors too; without one only the current
	/// statement is marked.
	/// </summary>
	static class ActiveStatementReadOnly
	{
		static IReadOnlyList<ActiveStatement> activeStatements = Array.Empty<ActiveStatement>();
		static TextArea textArea;
		static IReadOnlySectionProvider previousProvider;
		static int viewHookInstalled;

		public static void SetStatements(IEnumerable<ActiveStatement> statements)
		{
			activeStatements = statements?.ToArray() ?? Array.Empty<ActiveStatement>();
			EnsureViewHook();
			ApplyToActiveView();
		}

		public static void ApplyToActiveView()
		{
			var viewContent = SD.Workbench?.ActiveViewContent;
			if (viewContent == null || activeStatements.Count == 0) {
				RestoreProvider();
				return;
			}

			var editor = viewContent.GetService<ITextEditor>();
			if (editor is not AvalonEditTextEditorAdapter adapter) {
				RestoreProvider();
				return;
			}

			var document = editor.Document;
			if (document == null || editor.FileName == null) {
				RestoreProvider();
				return;
			}

			var segments = new List<ISegment>();
			foreach (var statement in activeStatements) {
				if (!string.Equals(statement.FileName.ToString(), editor.FileName.ToString(), StringComparison.OrdinalIgnoreCase))
					continue;
				if (statement.StartLine < 1 || statement.StartLine > document.LineCount)
					continue;
				var first = document.GetLineByNumber(statement.StartLine);
				var lastLine = Math.Min(Math.Max(statement.EndLine, statement.StartLine), document.LineCount);
				var last = document.GetLineByNumber(lastLine);
				segments.Add(new TextSegment {
					StartOffset = first.Offset,
					EndOffset = Math.Max(first.Offset, last.EndOffset)
				});
			}

			if (segments.Count == 0) {
				RestoreProvider();
				return;
			}

			var area = adapter.TextEditor.TextArea;
			if (!ReferenceEquals(textArea, area)) {
				RestoreProvider();
				textArea = area;
				previousProvider = area.ReadOnlySectionProvider;
			}
			area.ReadOnlySectionProvider = new ActiveStatementReadOnlySectionProvider(previousProvider, segments);
		}

		public static void Clear()
		{
			activeStatements = Array.Empty<ActiveStatement>();
			RestoreProvider();
		}

		static void RestoreProvider()
		{
			if (textArea != null)
				textArea.ReadOnlySectionProvider = previousProvider;
			textArea = null;
			previousProvider = null;
		}

		static void EnsureViewHook()
		{
			if (System.Threading.Interlocked.Exchange(ref viewHookInstalled, 1) != 0)
				return;
			try {
				var workbench = SD.Workbench;
				if (workbench != null)
					workbench.ActiveViewContentChanged += (_, _) => SD.MainThread.InvokeAsyncAndForget(ApplyToActiveView);
			} catch (Exception ex) {
				LoggingService.Warn("Debugger: could not observe active view changes for active statements: " + ex.Message);
			}
		}

		sealed class ActiveStatementReadOnlySectionProvider : IReadOnlySectionProvider
		{
			readonly IReadOnlySectionProvider inner;
			readonly IReadOnlyList<ISegment> activeStatements;

			public ActiveStatementReadOnlySectionProvider(IReadOnlySectionProvider inner, IReadOnlyList<ISegment> activeStatements)
			{
				this.inner = inner;
				this.activeStatements = activeStatements;
			}

			public bool CanInsert(int offset)
			{
				foreach (var statement in activeStatements) {
					if (offset >= statement.Offset && offset <= statement.EndOffset)
						return false;
				}
				return inner?.CanInsert(offset) ?? true;
			}

			public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
			{
				if (segment == null)
					return Enumerable.Empty<ISegment>();
				foreach (var statement in activeStatements) {
					if (segment.Offset < statement.EndOffset && segment.EndOffset > statement.Offset)
						return Enumerable.Empty<ISegment>();
				}
				return inner?.GetDeletableSegments(segment) ?? new[] { segment };
			}
		}
	}
}
