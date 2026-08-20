// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The VS ITextBuffer adapter over one AvalonEdit TextDocument (vs-editor-api.md section 11).
// Exactly one instance exists per document (see AvalonTextBufferRegistry). A whole AvalonEdit
// update group is translated into one VS buffer change transaction (section 12): the before
// snapshot is captured at UpdateStarted, and UpdateFinished produces the after snapshot, the
// change list (from ITextSourceVersion.GetChangesTo) and the VS event sequence
// Changing -> ChangedHighPriority -> Changed -> ChangedLowPriority -> PostChanged.

using System;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using System.Linq;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>An ITextBuffer backed by an AvalonEdit TextDocument.</summary>
public sealed class AvalonTextBuffer : ITextBuffer
{
	readonly TextDocument document;
	IContentType contentType;
	readonly PropertyCollection properties = new();
	int versionNumber;
	AvalonTextVersion currentVersion;
	AvalonTextSnapshot currentSnapshot;
	bool editInProgress;
	bool isInUpdateGroup;
	bool isFinalizing;
	AvalonTextSnapshot updateStartSnapshot;

	internal AvalonTextBuffer(TextDocument document, IContentType contentType)
	{
		this.document = document ?? throw new ArgumentNullException(nameof(document));
		this.contentType = contentType ?? throw new ArgumentNullException(nameof(contentType));

		currentVersion = new AvalonTextVersion(this, null, versionNumber, document.TextLength, AvalonTextChangeCollection.Empty);
		currentSnapshot = new AvalonTextSnapshot(this, document.CreateSnapshot(), currentVersion);
		currentVersion.SourceVersion = currentSnapshot.Source.Version;

		document.UpdateStarted += OnUpdateStarted;
		document.Changed += OnDocumentChanged;
		document.UpdateFinished += OnUpdateFinished;
	}

	/// <summary>The underlying AvalonEdit document; changes made through either surface are the
	/// same edits. Exposed so OpenDevelop (and extensions) can reach the real text engine.</summary>
	public TextDocument Document => document;

	/// <summary>The VS property bag extensions use to attach OpenDevelop-specific state.</summary>
	public PropertyCollection Properties => properties;

	public ITextSnapshot CurrentSnapshot => currentSnapshot;

	public IContentType ContentType => contentType;

	public bool EditInProgress => editInProgress;

	#region Events

	public event EventHandler<TextContentChangingEventArgs> Changing;
	public event EventHandler<TextContentChangedEventArgs> ChangedHighPriority;
	public event EventHandler<TextContentChangedEventArgs> Changed;
	public event EventHandler<TextContentChangedEventArgs> ChangedLowPriority;
	public event EventHandler PostChanged;
	public event EventHandler<ContentTypeChangedEventArgs> ContentTypeChanged;
	public event EventHandler<SnapshotSpanEventArgs> ReadOnlyRegionsChanged;

	#endregion

	#region Document event translation

	void OnUpdateStarted(object sender, EventArgs e)
	{
		isInUpdateGroup = true;
		updateStartSnapshot = currentSnapshot;
		var args = new TextContentChangingEventArgs(currentSnapshot, editTag: null, _ => { });
		Changing?.Invoke(this, args);
	}

	void OnDocumentChanged(object sender, DocumentChangeEventArgs e)
	{
		// A mutation outside an update group is still a change - finalize immediately.
		if (!isInUpdateGroup)
			FinalizeUpdate();
	}

	void OnUpdateFinished(object sender, EventArgs e)
	{
		isInUpdateGroup = false;
		FinalizeUpdate();
	}

	void FinalizeUpdate()
	{
		if (isFinalizing)
			return;
		isFinalizing = true;
		try {
			var beforeSnapshot = updateStartSnapshot ?? currentSnapshot;
			updateStartSnapshot = null;

			var afterSource = document.CreateSnapshot();
			int newVersionNumber = versionNumber + 1;
			var changes = BuildChanges(beforeSnapshot.Source, afterSource);
			var afterVersion = new AvalonTextVersion(this, currentVersion, newVersionNumber, afterSource.TextLength, changes);
			afterVersion.SourceVersion = afterSource.Version;
			currentVersion.SetNext(afterVersion);
			versionNumber = newVersionNumber;

			var afterSnapshot = new AvalonTextSnapshot(this, afterSource, afterVersion);
			currentVersion = afterVersion;
			currentSnapshot = afterSnapshot;

			var contentChanged = new TextContentChangedEventArgs(
				beforeSnapshot, afterSnapshot, EditOptions.None, editTag: null);
			ChangedHighPriority?.Invoke(this, contentChanged);
			Changed?.Invoke(this, contentChanged);
			ChangedLowPriority?.Invoke(this, contentChanged);
			PostChanged?.Invoke(this, EventArgs.Empty);
		} finally {
			isFinalizing = false;
		}
	}

	static INormalizedTextChangeCollection BuildChanges(ITextSource before, ITextSource after)
	{
		var collection = new AvalonTextChangeCollection();
		if (before.Version == null || after.Version == null || ReferenceEquals(before.Version, after.Version))
			return collection;
		foreach (var change in before.Version.GetChangesTo(after.Version))
			collection.Add(AvalonTextChange.FromTextChangeEventArgs(change));
		// GetChangesTo returns changes in application order; a VS-normalized collection is
		// ordered by position, so sort ascending before exposing it to consumers.
		var sorted = collection.OrderBy(change => change.OldPosition).ToList();
		collection.Clear();
		collection.AddRange(sorted);
		return collection;
	}

	#endregion

	#region Editing

	internal void BeginEdit() => editInProgress = true;

	internal void EndEdit() => editInProgress = false;

	public ITextEdit CreateEdit() => new AvalonTextEdit(this);

	public ITextEdit CreateEdit(EditOptions options, int? reiteratedVersionNumber, object editTag)
		=> new AvalonTextEdit(this);

	public ITextSnapshot Insert(int position, string text)
	{
		using var edit = CreateEdit();
		edit.Insert(position, text);
		return edit.Apply();
	}

	public ITextSnapshot Delete(Span deleteSpan)
	{
		using var edit = CreateEdit();
		edit.Delete(deleteSpan);
		return edit.Apply();
	}

	public ITextSnapshot Replace(Span replaceSpan, string replaceWith)
	{
		using var edit = CreateEdit();
		edit.Replace(replaceSpan, replaceWith);
		return edit.Apply();
	}

	#endregion

	#region Read-only (conservative: nothing is read-only in the spike)

	public IReadOnlyRegionEdit CreateReadOnlyRegionEdit() => new AvalonReadOnlyRegionEdit(this);

	public bool IsReadOnly(int position) => false;

	public bool IsReadOnly(int position, bool isEdit) => false;

	public bool IsReadOnly(Span span) => false;

	public bool IsReadOnly(Span span, bool isEdit) => false;

	public NormalizedSpanCollection GetReadOnlyExtents(Span span) => NormalizedSpanCollection.Empty;

	#endregion

	public void ChangeContentType(IContentType newContentType, object editTag)
	{
		if (newContentType == null)
			throw new ArgumentNullException(nameof(newContentType));
		if (ReferenceEquals(newContentType, contentType))
			return;
		var beforeContentType = contentType;
		contentType = newContentType;
		var args = new ContentTypeChangedEventArgs(
			currentSnapshot, currentSnapshot, beforeContentType, newContentType, editTag);
		ContentTypeChanged?.Invoke(this, args);
	}

	public bool CheckEditAccess() => true;

	public void TakeThreadOwnership()
	{
		// The document enforces its own thread affinity; nothing extra to claim here.
	}
}
