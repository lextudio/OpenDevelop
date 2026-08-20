// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextVersion-level tests: monotonic version numbering, Next chaining, change enumeration
// between versions, and offset migration across versions (vs-editor-api.md sections 10, 42, 74).

using System.Linq;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class VersionTests
{
	static AvalonTextBuffer CreateBuffer(string text)
		=> AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text), AvalonContentTypeRegistry.Text);

	[Fact]
	public void VersionNumbers_Are_Monotonic_And_Chained()
	{
		var buffer = CreateBuffer("abc");
		var v0 = buffer.CurrentSnapshot.Version;
		Assert.Equal(0, v0.VersionNumber);
		Assert.Null(v0.Next);

		buffer.Insert(1, "x");
		var v1 = buffer.CurrentSnapshot.Version;
		buffer.Insert(2, "y");
		var v2 = buffer.CurrentSnapshot.Version;

		Assert.Equal(1, v1.VersionNumber);
		Assert.Equal(2, v2.VersionNumber);
		Assert.Same(v1, v0.Next);
		Assert.Same(v2, v1.Next);
		Assert.Same(buffer, v1.TextBuffer);
		Assert.Equal("abc".Length + 2, v2.Length);
	}

	[Fact]
	public void ReiteratedVersionNumber_Equals_VersionNumber()
	{
		var buffer = CreateBuffer("abc");
		buffer.Insert(1, "x");
		var v1 = buffer.CurrentSnapshot.Version;
		Assert.Equal(v1.VersionNumber, v1.ReiteratedVersionNumber);
	}

	[Fact]
	public void Single_Insert_Yields_One_Change_With_Expected_Spans()
	{
		var buffer = CreateBuffer("class C {}");
		buffer.Insert(6, "partial ");

		var changes = buffer.CurrentSnapshot.Version.Changes;
		Assert.Equal(1, changes.Count);
		var change = changes[0];
		Assert.Equal(new Span(6, 0), change.OldSpan);
		Assert.Equal(new Span(6, 8), change.NewSpan);
		Assert.Equal("", change.OldText);
		Assert.Equal("partial ", change.NewText);
		Assert.Equal(8, change.Delta);
	}

	[Fact]
	public void Each_Version_Owns_Its_Own_Changes()
	{
		var buffer = CreateBuffer("abc");
		var v0 = buffer.CurrentSnapshot.Version;

		buffer.Insert(1, "x");
		var v1 = buffer.CurrentSnapshot.Version;
		buffer.Insert(2, "y");
		var v2 = buffer.CurrentSnapshot.Version;

		Assert.Equal(0, v0.Changes.Count);
		Assert.Equal(1, v1.Changes.Count);
		Assert.Equal("x", v1.Changes[0].NewText);
		Assert.Equal(1, v2.Changes.Count);
		Assert.Equal("y", v2.Changes[0].NewText);
	}

	[Fact]
	public void Replace_Change_Carries_Old_And_New_Text()
	{
		var buffer = CreateBuffer("hello world");
		buffer.Replace(new Span(0, 5), "bye");
		var change = buffer.CurrentSnapshot.Version.Changes[0];
		Assert.Equal("hello", change.OldText);
		Assert.Equal("bye", change.NewText);
		Assert.Equal(new Span(0, 5), change.OldSpan);
		Assert.Equal(new Span(0, 3), change.NewSpan);
		Assert.Equal(-2, change.Delta);
	}

	[Fact]
	public void Multi_Change_Edit_Produces_Multiple_Changes()
	{
		var buffer = CreateBuffer("abcdef");
		using var edit = buffer.CreateEdit();
		edit.Replace(0, 2, "AA");
		edit.Replace(4, 2, "ZZ");
		edit.Apply();

		var changes = buffer.CurrentSnapshot.Version.Changes;
		Assert.Equal(2, changes.Count);
		Assert.Equal("ab", changes[0].OldText);
		Assert.Equal("ef", changes[1].OldText);
	}

	[Fact]
	public void Version_LineCountDelta_Reflects_Inserted_Newlines()
	{
		var buffer = CreateBuffer("one two");
		buffer.Replace(new Span(3, 1), "\n");
		var change = buffer.CurrentSnapshot.Version.Changes[0];
		Assert.Equal(1, change.LineCountDelta);
	}

	[Fact]
	public void Offset_Moves_Between_Old_And_New_Version()
	{
		var buffer = CreateBuffer("abc");
		var snapshot0 = buffer.CurrentSnapshot;
		buffer.Insert(1, "XYZ");
		var snapshot1 = buffer.CurrentSnapshot;

		// A point at offset 1 (before the insertion point is at 1, the inserted text begins at 1)
		// tracked positively moves to 1 + 3 = 4.
		var point = snapshot0.CreateTrackingPoint(1, PointTrackingMode.Positive);
		Assert.Equal(4, point.GetPosition(snapshot1));
		Assert.Equal(4, point.GetPosition(snapshot1.Version));

		// The same point resolved against the version it was created on stays put.
		Assert.Equal(1, point.GetPosition(snapshot0));
	}
}
