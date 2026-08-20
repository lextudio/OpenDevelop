// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The normalized change list an ITextVersion.Changes returns. VS guarantees the list is
// normalized (non-overlapping, ascending); the AvalonEdit changes produced by
// ITextSourceVersion.GetChangesTo already come ordered and non-overlapping for a single update
// group, so a thin list-backed implementation is sufficient for the spike.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A list of non-overlapping text changes between two versions.</summary>
public sealed class AvalonTextChangeCollection : List<ITextChange>, INormalizedTextChangeCollection
{
	/// <summary>An empty change collection, shared by the initial version of every buffer.</summary>
	public static readonly INormalizedTextChangeCollection Empty = new AvalonTextChangeCollection();

	public AvalonTextChangeCollection()
	{
	}

	public AvalonTextChangeCollection(IEnumerable<ITextChange> changes)
		: base(changes ?? Enumerable.Empty<ITextChange>())
	{
	}

	public bool IncludesLineChanges => this.Any(change => change.LineCountDelta != 0);
}
