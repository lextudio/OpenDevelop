// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextDocument / ITextDocumentFactoryService tests: load, dirty tracking, save/save-as/
// save-copy, reload, rename, encoding, and buffer->document lookup (vs-editor-api.md sections
// 31, 39, 51).

using System;
using System.IO;
using System.Text;

using Microsoft.VisualStudio.Text;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class TextDocumentTests : IDisposable
{
	readonly AvalonTextDocumentFactoryService factory = new();
	readonly string tempDir;

	public TextDocumentTests()
	{
		tempDir = Path.Combine(Path.GetTempPath(), "vse-od-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDir);
	}

	public void Dispose()
	{
		try {
			Directory.Delete(tempDir, recursive: true);
		} catch {
			// best-effort cleanup
		}
	}

	string NewFile(string relative, string content)
	{
		var path = Path.Combine(tempDir, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return path;
	}

	[Fact]
	public void CreateAndLoad_Reads_File_Into_Buffer()
	{
		var path = NewFile("a.txt", "hello");
		var document = factory.CreateAndLoadTextDocument(path, AvalonContentTypeRegistry.Text);

		Assert.Equal(path, document.FilePath);
		Assert.Same(document, factory.TryGetTextDocument(document.TextBuffer, out var found) ? found : null);
		Assert.Equal("hello", document.TextBuffer.CurrentSnapshot.GetText());
		Assert.False(document.IsDirty);
	}

	[Fact]
	public void Buffer_Edit_Marks_Document_Dirty_And_Raises_Event()
	{
		var path = NewFile("a.txt", "hello");
		var document = factory.CreateAndLoadTextDocument(path, AvalonContentTypeRegistry.Text);
		int dirtyEvents = 0;
		document.DirtyStateChanged += (_, _) => dirtyEvents++;

		document.TextBuffer.Insert(5, " world");

		Assert.True(document.IsDirty);
		Assert.Equal(1, dirtyEvents);
	}

	[Fact]
	public void Save_Writes_File_And_Clears_Dirty()
	{
		var path = NewFile("a.txt", "hello");
		var document = factory.CreateAndLoadTextDocument(path, AvalonContentTypeRegistry.Text);
		document.TextBuffer.Insert(5, " world");

		FileActionTypes? lastAction = null;
		document.FileActionOccurred += (_, e) => lastAction = e.FileActionType;
		document.Save();

		Assert.Equal("hello world", File.ReadAllText(path));
		Assert.False(document.IsDirty);
		Assert.Equal(FileActionTypes.ContentSavedToDisk, lastAction);
	}

	[Fact]
	public void SaveAs_Updates_Path_Renames_And_Writes()
	{
		var path = NewFile("a.txt", "hello");
		var document = factory.CreateAndLoadTextDocument(path, AvalonContentTypeRegistry.Text);
		var newPath = Path.Combine(tempDir, "b.txt");

		var actions = new System.Collections.Generic.List<FileActionTypes>();
		document.FileActionOccurred += (_, e) => actions.Add(e.FileActionType);
		document.SaveAs(newPath, overwrite: true);

		Assert.Equal(newPath, document.FilePath);
		Assert.True(File.Exists(newPath));
		Assert.Equal("hello", File.ReadAllText(newPath));
		Assert.False(document.IsDirty);
		Assert.Contains(FileActionTypes.DocumentRenamed, actions);
		Assert.Contains(FileActionTypes.ContentSavedToDisk, actions);
	}

	[Fact]
	public void SaveCopy_Writes_Without_Changing_Path_Or_Dirty()
	{
		var path = NewFile("a.txt", "hello");
		var document = factory.CreateAndLoadTextDocument(path, AvalonContentTypeRegistry.Text);
		document.TextBuffer.Insert(5, " world");
		var copyPath = Path.Combine(tempDir, "copy.txt");

		document.SaveCopy(copyPath, overwrite: true);

		Assert.Equal("hello world", File.ReadAllText(copyPath));
		Assert.Equal(path, document.FilePath);
		Assert.True(document.IsDirty);
	}

	[Fact]
	public void Reload_ReReads_File_And_Clears_Dirty()
	{
		var path = NewFile("a.txt", "hello");
		var document = factory.CreateAndLoadTextDocument(path, AvalonContentTypeRegistry.Text);
		document.TextBuffer.Insert(5, " world");
		File.WriteAllText(path, "reloaded on disk");

		FileActionTypes? lastAction = null;
		document.FileActionOccurred += (_, e) => lastAction = e.FileActionType;
		var result = document.Reload();

		Assert.Equal(ReloadResult.Succeeded, result);
		Assert.Equal("reloaded on disk", document.TextBuffer.CurrentSnapshot.GetText());
		Assert.False(document.IsDirty);
		Assert.Equal(FileActionTypes.ContentLoadedFromDisk, lastAction);
	}

	[Fact]
	public void Rename_Updates_FilePath_And_Raises_DocumentRenamed()
	{
		var path = NewFile("a.txt", "hello");
		var document = factory.CreateAndLoadTextDocument(path, AvalonContentTypeRegistry.Text);
		var newPath = Path.Combine(tempDir, "renamed.txt");

		FileActionTypes? lastAction = null;
		document.FileActionOccurred += (_, e) => lastAction = e.FileActionType;
		document.Rename(newPath);

		Assert.Equal(newPath, document.FilePath);
		Assert.Equal(FileActionTypes.DocumentRenamed, lastAction);
	}

	[Fact]
	public void Changing_Encoding_Raises_EncodingChanged()
	{
		var path = NewFile("a.txt", "hello");
		var document = factory.CreateAndLoadTextDocument(path, AvalonContentTypeRegistry.Text);
		Encoding? oldEncoding = null;
		Encoding? newEncoding = null;
		document.EncodingChanged += (_, e) => {
			oldEncoding = e.OldEncoding;
			newEncoding = e.NewEncoding;
		};

		document.Encoding = Encoding.Unicode;

		Assert.Equal(Encoding.UTF8, oldEncoding);
		Assert.Equal(Encoding.Unicode, newEncoding);
		Assert.Equal(Encoding.Unicode, document.Encoding);
	}

	[Fact]
	public void CreateTextDocument_Wraps_Existing_Buffer()
	{
		var buffer = AvalonTextBufferRegistry.GetOrCreate(new ICSharpCode.AvalonEdit.Document.TextDocument("x"), AvalonContentTypeRegistry.Text);
		var document = factory.CreateTextDocument(buffer, Path.Combine(tempDir, "wrapped.txt"));
		Assert.Same(buffer, document.TextBuffer);
		Assert.True(factory.TryGetTextDocument(buffer, out var found));
		Assert.Same(document, found);
	}
}
