// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// vs-editor-api.md section 41's proof-of-concept plus section 42's snapshot test list.

using System.Threading.Tasks;

using ICSharpCode.AvalonEdit.Document;
using LeXtudio.OpenDevelop.VSEditor;
using Microsoft.VisualStudio.Text;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class SnapshotTests
{
	static AvalonTextBuffer CreateBuffer(string text = "class C {}")
	{
		var document = new TextDocument(text);
		return AvalonTextBufferRegistry.GetOrCreate(document, AvalonContentTypeRegistry.Text);
	}

	[Fact]
	public void CurrentSnapshotMirrorsTextDocument()
	{
		var buffer = CreateBuffer("hello");
		Assert.Equal("hello", buffer.CurrentSnapshot.GetText());
		Assert.Equal(buffer.Document.TextLength, buffer.CurrentSnapshot.Length);
	}

	[Fact]
	public void OldSnapshotRemainsImmutableAfterEditing()
	{
		var buffer = CreateBuffer("class C {}");
		var before = buffer.CurrentSnapshot;

		using (var edit = buffer.CreateEdit()) {
			edit.Insert(6, "partial ");
			edit.Apply();
		}

		Assert.Equal("class C {}", before.GetText());
		Assert.Equal("class partial C {}", buffer.CurrentSnapshot.GetText());
		Assert.NotSame(before, buffer.CurrentSnapshot);
	}

	[Fact]
	public void TwoSnapshotsCompareAsDifferentVersions()
	{
		var buffer = CreateBuffer("abc");
		var before = buffer.CurrentSnapshot;
		buffer.Insert(0, "x");
		var after = buffer.CurrentSnapshot;

		Assert.True(after.Version.VersionNumber > before.Version.VersionNumber);
	}

	[Fact]
	public async Task SnapshotTextCanBeReadOnBackgroundThread()
	{
		var buffer = CreateBuffer("background snapshot text");
		var snapshot = buffer.CurrentSnapshot;

		var text = await Task.Run(() => snapshot.GetText());

		Assert.Equal("background snapshot text", text);
	}

	[Fact]
	public void LineEnumerationIsStable()
	{
		var buffer = CreateBuffer("one\ntwo\nthree");
		var snapshot = buffer.CurrentSnapshot;

		Assert.Equal(3, snapshot.LineCount);
		Assert.Equal("one", snapshot.GetLineFromLineNumber(0).GetText());
		Assert.Equal("two", snapshot.GetLineFromLineNumber(1).GetText());
		Assert.Equal("three", snapshot.GetLineFromLineNumber(2).GetText());
	}

	[Fact]
	public void ChangesBetweenVersionsCanBeEnumerated()
	{
		var buffer = CreateBuffer("abcdef");
		var before = buffer.CurrentSnapshot;
		buffer.Replace(new Span(1, 2), "XYZ");
		var after = buffer.CurrentSnapshot;

		var changes = after.Version.Changes;
		Assert.NotEmpty(changes);
	}
}
