// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// vs-editor-api.md section 42's tracking point/span edge-case matrix. "Do not guess these
// semantics. Encode them as tests from the beginning" (section 15).

using ICSharpCode.AvalonEdit.Document;
using LeXtudio.OpenDevelop.VSEditor;
using Microsoft.VisualStudio.Text;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class TrackingTests
{
	static AvalonTextBuffer CreateBuffer(string text) =>
		AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text), AvalonContentTypeRegistry.Text);

	[Fact]
	public void PositiveTrackingPointMovesAfterInsertionAtItself()
	{
		var buffer = CreateBuffer("abcdef");
		var point = buffer.CurrentSnapshot.CreateTrackingPoint(3, PointTrackingMode.Positive);

		buffer.Insert(3, "XYZ");

		Assert.Equal(6, point.GetPosition(buffer.CurrentSnapshot));
	}

	[Fact]
	public void NegativeTrackingPointStaysBeforeInsertionAtItself()
	{
		var buffer = CreateBuffer("abcdef");
		var point = buffer.CurrentSnapshot.CreateTrackingPoint(3, PointTrackingMode.Negative);

		buffer.Insert(3, "XYZ");

		Assert.Equal(3, point.GetPosition(buffer.CurrentSnapshot));
	}

	[Fact]
	public void TrackingPointMovesWithInsertionBeforeIt()
	{
		var buffer = CreateBuffer("abcdef");
		var point = buffer.CurrentSnapshot.CreateTrackingPoint(4, PointTrackingMode.Positive);

		buffer.Insert(0, "XYZ");

		Assert.Equal(7, point.GetPosition(buffer.CurrentSnapshot));
	}

	[Fact]
	public void TrackingPointUnaffectedByInsertionAfterIt()
	{
		var buffer = CreateBuffer("abcdef");
		var point = buffer.CurrentSnapshot.CreateTrackingPoint(2, PointTrackingMode.Positive);

		buffer.Insert(4, "XYZ");

		Assert.Equal(2, point.GetPosition(buffer.CurrentSnapshot));
	}

	[Fact]
	public void TrackingPointClampsWhenTextBeforeItIsDeleted()
	{
		var buffer = CreateBuffer("abcdef");
		var point = buffer.CurrentSnapshot.CreateTrackingPoint(4, PointTrackingMode.Positive);

		buffer.Delete(new Span(0, 2));

		Assert.Equal(2, point.GetPosition(buffer.CurrentSnapshot));
	}

	[Fact]
	public void TrackingPointClampsWhenItsRangeIsDeleted()
	{
		var buffer = CreateBuffer("abcdef");
		var point = buffer.CurrentSnapshot.CreateTrackingPoint(3, PointTrackingMode.Positive);

		buffer.Delete(new Span(1, 4));

		Assert.Equal(1, point.GetPosition(buffer.CurrentSnapshot));
	}

	[Fact]
	public void TrackingPointSurvivesMultipleEditsInOneTransaction()
	{
		var buffer = CreateBuffer("abcdefghij");
		var point = buffer.CurrentSnapshot.CreateTrackingPoint(5, PointTrackingMode.Positive);

		using (var edit = buffer.CreateEdit()) {
			edit.Insert(0, "12");
			edit.Insert(8, "34");
			edit.Apply();
		}

		Assert.Equal(7, point.GetPosition(buffer.CurrentSnapshot));
	}

	[Theory]
	[InlineData(SpanTrackingMode.EdgeExclusive)]
	[InlineData(SpanTrackingMode.EdgeInclusive)]
	[InlineData(SpanTrackingMode.EdgePositive)]
	[InlineData(SpanTrackingMode.EdgeNegative)]
	public void TrackingSpanUnaffectedByEditInsideOnlyGrows(SpanTrackingMode mode)
	{
		// original: ABCDEF, span: CDE (offsets 2..5)
		var buffer = CreateBuffer("ABCDEF");
		var span = buffer.CurrentSnapshot.CreateTrackingSpan(new Span(2, 3), mode);

		buffer.Insert(3, "X"); // insert inside the span

		var resolved = span.GetSpan(buffer.CurrentSnapshot);
		Assert.Equal(2, resolved.Start.Position);
		Assert.Equal(6, resolved.End.Position);
		Assert.Equal("CXDE", resolved.GetText());
	}

	[Fact]
	public void EdgeExclusiveSpanDoesNotGrowWhenInsertingExactlyAtStart()
	{
		var buffer = CreateBuffer("ABCDEF");
		var span = buffer.CurrentSnapshot.CreateTrackingSpan(new Span(2, 3), SpanTrackingMode.EdgeExclusive);

		buffer.Insert(2, "XYZ");

		var resolved = span.GetSpan(buffer.CurrentSnapshot);
		Assert.Equal(5, resolved.Start.Position);
		Assert.Equal("CDE", resolved.GetText());
	}

	[Fact]
	public void EdgeInclusiveSpanGrowsWhenInsertingExactlyAtStart()
	{
		var buffer = CreateBuffer("ABCDEF");
		var span = buffer.CurrentSnapshot.CreateTrackingSpan(new Span(2, 3), SpanTrackingMode.EdgeInclusive);

		buffer.Insert(2, "XYZ");

		var resolved = span.GetSpan(buffer.CurrentSnapshot);
		Assert.Equal(2, resolved.Start.Position);
		Assert.Equal("XYZCDE", resolved.GetText());
	}

	[Fact]
	public void EdgeExclusiveSpanDoesNotGrowWhenInsertingExactlyAtEnd()
	{
		var buffer = CreateBuffer("ABCDEF");
		var span = buffer.CurrentSnapshot.CreateTrackingSpan(new Span(2, 3), SpanTrackingMode.EdgeExclusive);

		buffer.Insert(5, "XYZ");

		var resolved = span.GetSpan(buffer.CurrentSnapshot);
		Assert.Equal("CDE", resolved.GetText());
	}

	[Fact]
	public void EdgeInclusiveSpanGrowsWhenInsertingExactlyAtEnd()
	{
		var buffer = CreateBuffer("ABCDEF");
		var span = buffer.CurrentSnapshot.CreateTrackingSpan(new Span(2, 3), SpanTrackingMode.EdgeInclusive);

		buffer.Insert(5, "XYZ");

		var resolved = span.GetSpan(buffer.CurrentSnapshot);
		Assert.Equal("CDEXYZ", resolved.GetText());
	}

	[Fact]
	public void SpanCollapsesWhenItsFullRangeIsDeleted()
	{
		var buffer = CreateBuffer("ABCDEF");
		var span = buffer.CurrentSnapshot.CreateTrackingSpan(new Span(2, 3), SpanTrackingMode.EdgeExclusive);

		buffer.Delete(new Span(2, 3));

		var resolved = span.GetSpan(buffer.CurrentSnapshot);
		Assert.Equal(0, resolved.Length);
	}

	[Fact]
	public void SpanShrinksWhenItsStartBoundaryIsDeletedInto()
	{
		var buffer = CreateBuffer("ABCDEF");
		var span = buffer.CurrentSnapshot.CreateTrackingSpan(new Span(2, 3), SpanTrackingMode.EdgeExclusive);

		buffer.Delete(new Span(0, 3)); // deletes "ABC", eating into the span's start

		var resolved = span.GetSpan(buffer.CurrentSnapshot);
		Assert.Equal("DE", resolved.GetText());
	}
}
