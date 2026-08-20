// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// A VS ITextDocument over one AvalonTextBuffer + file path: loads/saves/reloads the file,
// tracks dirty state from buffer changes, and reports file actions. For the spike this uses
// plain synchronous file I/O; OpenDevelop integration (section 67) later delegates save/reload
// to OpenDevelop's own file-management so dirty state stays in one place (section 51).

using System;
using System.IO;
using System.Text;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A file-backed VS text document over an AvalonEdit document.</summary>
public sealed class AvalonTextDocument : ITextDocument
{
	readonly AvalonTextBuffer buffer;
	string filePath;
	Encoding encoding;
	bool isDirty;
	bool isReloading;
	DateTime lastSavedTime;
	DateTime lastContentModifiedTime;
	EncoderFallback encoderFallback = EncoderFallback.ReplacementFallback;

	internal AvalonTextDocument(AvalonTextBuffer buffer, string filePath, Encoding encoding = null)
	{
		this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
		this.filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
		this.encoding = encoding ?? Encoding.UTF8;
		lastContentModifiedTime = DateTime.Now;
		buffer.Changed += OnBufferChanged;
	}

	public ITextBuffer TextBuffer => buffer;

	public string FilePath => filePath;

	public Encoding Encoding {
		get => encoding;
		set {
			if (value == null)
				throw new ArgumentNullException(nameof(value));
			if (ReferenceEquals(value, encoding))
				return;
			var oldEncoding = encoding;
			encoding = value;
			EncodingChanged?.Invoke(this, new EncodingChangedEventArgs(oldEncoding, value));
		}
	}

	public bool IsDirty => isDirty;

	public bool IsReloading => isReloading;

	public DateTime LastSavedTime => lastSavedTime;

	public DateTime LastContentModifiedTime => lastContentModifiedTime;

	public event EventHandler DirtyStateChanged;
	public event EventHandler<EncodingChangedEventArgs> EncodingChanged;
	public event EventHandler<TextDocumentFileActionEventArgs> FileActionOccurred;

	/// <summary>Detaches from the buffer; the underlying TextDocument lives as long as the buffer.</summary>
	public void Dispose()
	{
		buffer.Changed -= OnBufferChanged;
	}

	void OnBufferChanged(object sender, TextContentChangedEventArgs e)
	{
		// Changes made while reloading are the reload itself, not user edits.
		if (isReloading)
			return;
		lastContentModifiedTime = DateTime.Now;
		isDirty = true;
		DirtyStateChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Save()
	{
		File.WriteAllText(filePath, buffer.CurrentSnapshot.GetText(), EncodingWithFallback());
		lastSavedTime = DateTime.Now;
		isDirty = false;
		FileActionOccurred?.Invoke(this,
			new TextDocumentFileActionEventArgs(filePath, DateTime.Now, FileActionTypes.ContentSavedToDisk));
		DirtyStateChanged?.Invoke(this, EventArgs.Empty);
	}

	public void SaveAs(string filePath, bool overwrite)
		=> SaveAs(filePath, overwrite, createFolder: false, newContentType: null);

	public void SaveAs(string filePath, bool overwrite, bool createFolder)
		=> SaveAs(filePath, overwrite, createFolder, newContentType: null);

	public void SaveAs(string filePath, bool overwrite, IContentType newContentType)
		=> SaveAs(filePath, overwrite, createFolder: false, newContentType);

	public void SaveAs(string filePath, bool overwrite, bool createFolder, IContentType newContentType)
	{
		if (filePath == null)
			throw new ArgumentNullException(nameof(filePath));
		if (createFolder)
			Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
		File.WriteAllText(filePath, buffer.CurrentSnapshot.GetText(), EncodingWithFallback());
		var oldFilePath = this.filePath;
		this.filePath = filePath;
		lastSavedTime = DateTime.Now;
		isDirty = false;
		if (newContentType != null)
			buffer.ChangeContentType(newContentType, editTag: null);
		FileActionOccurred?.Invoke(this,
			new TextDocumentFileActionEventArgs(oldFilePath, filePath, DateTime.Now, FileActionTypes.DocumentRenamed));
		FileActionOccurred?.Invoke(this,
			new TextDocumentFileActionEventArgs(filePath, DateTime.Now, FileActionTypes.ContentSavedToDisk));
		DirtyStateChanged?.Invoke(this, EventArgs.Empty);
	}

	public void SaveCopy(string filePath, bool overwrite)
		=> SaveCopy(filePath, overwrite, createFolder: false);

	public void SaveCopy(string filePath, bool overwrite, bool createFolder)
	{
		if (filePath == null)
			throw new ArgumentNullException(nameof(filePath));
		if (createFolder)
			Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
		File.WriteAllText(filePath, buffer.CurrentSnapshot.GetText(), EncodingWithFallback());
		FileActionOccurred?.Invoke(this,
			new TextDocumentFileActionEventArgs(filePath, DateTime.Now, FileActionTypes.ContentSavedToDisk));
	}

	public void Rename(string newFilePath)
	{
		if (newFilePath == null)
			throw new ArgumentNullException(nameof(newFilePath));
		var oldFilePath = filePath;
		filePath = newFilePath;
		FileActionOccurred?.Invoke(this,
			new TextDocumentFileActionEventArgs(oldFilePath, newFilePath, DateTime.Now, FileActionTypes.DocumentRenamed));
	}

	public ReloadResult Reload() => Reload(EditOptions.None);

	public ReloadResult Reload(EditOptions options)
	{
		isReloading = true;
		try {
			var text = File.ReadAllText(filePath, EncodingWithFallback());
			buffer.Document.BeginUpdate();
			try {
				buffer.Document.Replace(0, buffer.Document.TextLength, text);
			} finally {
				buffer.Document.EndUpdate();
			}
			isDirty = false;
			lastSavedTime = DateTime.Now;
			FileActionOccurred?.Invoke(this,
				new TextDocumentFileActionEventArgs(filePath, DateTime.Now, FileActionTypes.ContentLoadedFromDisk));
			DirtyStateChanged?.Invoke(this, EventArgs.Empty);
			return ReloadResult.Succeeded;
		} finally {
			isReloading = false;
		}
	}

	public void SetEncoderFallback(EncoderFallback fallback)
		=> encoderFallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

	public void UpdateDirtyState(bool isDirty, DateTime lastContentModifiedTime)
	{
		this.isDirty = isDirty;
		this.lastContentModifiedTime = lastContentModifiedTime;
		DirtyStateChanged?.Invoke(this, EventArgs.Empty);
	}

	Encoding EncodingWithFallback()
	{
		if (encoderFallback == null || encoderFallback.Equals(encoding.EncoderFallback))
			return encoding;
		var withFallback = (Encoding)encoding.Clone();
		withFallback.EncoderFallback = encoderFallback;
		return withFallback;
	}
}
