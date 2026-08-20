// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IProjectionBuffer/IElisionBuffer tests (vs-editor-api.md section 32). Pure text-model logic -
// no window/layout needed, unlike ViewLineTests's ITextViewLine geometry.

using System.Linq;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class ProjectionTests
{
	static AvalonTextBuffer CreateBuffer(string text)
		=> AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text), AvalonContentTypeRegistry.Text);

	static AvalonProjectionBufferFactoryService Factory => new(AvalonContentTypeRegistry.Instance);

	[Fact]
	public void Projection_Concatenates_Spans_From_Multiple_Source_Buffers()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);

		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA, "---", spanB }, ProjectionBufferOptions.None);

		Assert.Equal("AAA---BBB", projection.CurrentSnapshot.GetText());
	}

	[Fact]
	public void Projection_Reflects_Edits_To_A_Source_Buffer()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA, spanB }, ProjectionBufferOptions.None);

		a.Insert(1, "XX");

		Assert.Equal("AXXAABBB", projection.CurrentSnapshot.GetText());
	}

	[Fact]
	public void MapToSourceSnapshots_Resolves_A_Projection_Point_To_Its_Source_Buffer_And_Offset()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA, spanB }, ProjectionBufferOptions.None);

		var sourcePoint = projection.CurrentSnapshot.MapToSourceSnapshot(4); // 'B' at index 1 of "BBB"

		Assert.Same(b.CurrentSnapshot, sourcePoint.Snapshot);
		Assert.Equal(1, sourcePoint.Position);
	}

	[Fact]
	public void MapFromSourceSnapshot_Resolves_A_Source_Point_Back_To_The_Projection()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA, spanB }, ProjectionBufferOptions.None);

		var mapped = projection.CurrentSnapshot.MapFromSourceSnapshot(new SnapshotPoint(b.CurrentSnapshot, 1), PositionAffinity.Successor);

		Assert.Equal(4, mapped.Value.Position);
	}

	[Fact]
	public void MapFromSourceSnapshot_Returns_Null_For_A_Buffer_Not_In_The_Projection()
	{
		var a = CreateBuffer("AAA");
		var other = CreateBuffer("ZZZ");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA }, ProjectionBufferOptions.None);

		var mapped = projection.CurrentSnapshot.MapFromSourceSnapshot(new SnapshotPoint(other.CurrentSnapshot, 0), PositionAffinity.Successor);

		Assert.Null(mapped);
	}

	[Fact]
	public void Editing_Inside_A_Single_Segment_Writes_Through_To_The_Source_Buffer()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA, spanB }, ProjectionBufferOptions.None);

		projection.Insert(1, "XX"); // inside the "AAA" segment

		Assert.Equal("AXXAABBB", projection.CurrentSnapshot.GetText());
		Assert.Equal("AXXAA", a.CurrentSnapshot.GetText());
		Assert.Equal("BBB", b.CurrentSnapshot.GetText());
	}

	[Fact]
	public void Editing_Across_A_Segment_Boundary_Throws()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA, spanB }, ProjectionBufferOptions.None);

		Assert.Throws<System.NotSupportedException>(() => projection.Replace(new Span(2, 2), "xy"));
	}

	[Fact]
	public void InsertSpan_Adds_A_New_Segment_At_The_Given_Position()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA }, ProjectionBufferOptions.None);

		projection.InsertSpan(1, spanB);

		Assert.Equal("AAABBB", projection.CurrentSnapshot.GetText());
		Assert.Equal(2, projection.CurrentSnapshot.SpanCount);
	}

	[Fact]
	public void DeleteSpans_Removes_A_Segment()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA, spanB }, ProjectionBufferOptions.None);

		projection.DeleteSpans(0, 1);

		Assert.Equal("BBB", projection.CurrentSnapshot.GetText());
		Assert.Equal(1, projection.CurrentSnapshot.SpanCount);
	}

	[Fact]
	public void SourceSpansChanged_Fires_On_InsertSpan()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA }, ProjectionBufferOptions.None);
		int raised = 0;
		((IProjectionBuffer)projection).SourceSpansChanged += (_, __) => raised++;

		projection.InsertSpan(1, spanB);

		Assert.Equal(1, raised);
	}

	[Fact]
	public void Projection_TrackingPoint_Follows_A_Structural_Span_Insertion()
	{
		var a = CreateBuffer("AAA");
		var b = CreateBuffer("BBB");
		var spanA = a.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var spanB = b.CurrentSnapshot.CreateTrackingSpan(new Span(0, 3), SpanTrackingMode.EdgeExclusive);
		var projection = Factory.CreateProjectionBuffer(null, new object[] { spanA }, ProjectionBufferOptions.None);
		var point = projection.CurrentSnapshot.CreateTrackingPoint(1, PointTrackingMode.Positive);

		projection.InsertSpan(0, spanB); // "BBB" now precedes "AAA"

		Assert.Equal(4, point.GetPosition(projection.CurrentSnapshot));
	}

	[Fact]
	public void Elision_Hides_The_Elided_Range_From_The_Snapshot_Text()
	{
		var source = CreateBuffer("0123456789");
		var factory = Factory;
		var exposed = new NormalizedSnapshotSpanCollection(new[] {
			new SnapshotSpan(source.CurrentSnapshot, 0, 3),
			new SnapshotSpan(source.CurrentSnapshot, 7, 3),
		});

		var elision = factory.CreateElisionBuffer(null, exposed, ElisionBufferOptions.None);

		Assert.Equal("012789", elision.CurrentSnapshot.GetText());
	}

	[Fact]
	public void Elision_ExpandSpans_Reveals_Previously_Elided_Text()
	{
		var source = CreateBuffer("0123456789");
		var exposed = new NormalizedSnapshotSpanCollection(new SnapshotSpan(source.CurrentSnapshot, 0, 3));
		var elision = Factory.CreateElisionBuffer(null, exposed, ElisionBufferOptions.None);
		Assert.Equal("012", elision.CurrentSnapshot.GetText());

		elision.ExpandSpans(new NormalizedSpanCollection(new Span(3, 7)));

		Assert.Equal("0123456789", elision.CurrentSnapshot.GetText());
	}

	[Fact]
	public void Elision_Reflects_Edits_To_The_Source_Buffer()
	{
		var source = CreateBuffer("0123456789");
		var exposed = new NormalizedSnapshotSpanCollection(new[] {
			new SnapshotSpan(source.CurrentSnapshot, 0, 3),
			new SnapshotSpan(source.CurrentSnapshot, 7, 3),
		});
		var elision = Factory.CreateElisionBuffer(null, exposed, ElisionBufferOptions.None);

		source.Insert(1, "X"); // "0123456789" -> "0X123456789", inside the first visible span [0,3)

		Assert.Equal("0X12789", elision.CurrentSnapshot.GetText());
	}

	[Fact]
	public void Elision_MapFromSourceSnapshotToNearest_Snaps_Out_Of_An_Elided_Range()
	{
		var source = CreateBuffer("0123456789");
		var exposed = new NormalizedSnapshotSpanCollection(new[] {
			new SnapshotSpan(source.CurrentSnapshot, 0, 3),
			new SnapshotSpan(source.CurrentSnapshot, 7, 3),
		});
		var elision = Factory.CreateElisionBuffer(null, exposed, ElisionBufferOptions.None);

		// Source offset 5 is inside the elided "34567"->wait, elided range is [3,7) - offset 5 is elided.
		var nearest = elision.CurrentSnapshot.MapFromSourceSnapshotToNearest(new SnapshotPoint(source.CurrentSnapshot, 5));

		// Should snap to the start of the next visible span ("789" begins at projection offset 3).
		Assert.Equal(3, nearest.Position);
	}
}
