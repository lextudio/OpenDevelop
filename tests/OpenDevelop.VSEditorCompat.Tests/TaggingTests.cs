// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITagAggregator<T>/ITaggerProvider tests (vs-editor-api.md section 25): a provider registered
// with EditorCompositionHost for a content type is found by AvalonTagAggregatorFactoryService,
// its tagger's spans surface through the aggregator, and TagsChanged forwards.

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class TaggingTests
{
	sealed class TestTag : ITag
	{
		public TestTag(string name) => Name = name;
		public string Name { get; }
	}

	sealed class FixedSpanTagger : ITagger<TestTag>
	{
		public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

		public void RaiseTagsChanged(SnapshotSpan span) => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(span));

		public IEnumerable<ITagSpan<TestTag>> GetTags(NormalizedSnapshotSpanCollection spans)
		{
			foreach (var span in spans)
				yield return new TagSpan<TestTag>(span, new TestTag("fixed"));
		}
	}

	sealed class FixedSpanTaggerProvider : ITaggerProvider
	{
		public FixedSpanTagger LastCreated { get; private set; }

		public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
		{
			var tagger = new FixedSpanTagger();
			LastCreated = tagger;
			return tagger as ITagger<T>;
		}
	}

	static AvalonTextBuffer CreateBuffer(string text)
		=> AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text), AvalonContentTypeRegistry.Text);

	[Fact]
	public void Aggregator_Surfaces_Tags_From_Registered_Provider()
	{
		var provider = new FixedSpanTaggerProvider();
		using var registration = EditorCompositionHost.RegisterTaggerProvider("text", provider);

		var buffer = CreateBuffer("hello world");
		var factory = new AvalonTagAggregatorFactoryService();
		using var aggregator = factory.CreateTagAggregator<TestTag>(buffer);

		var tags = aggregator.GetTags(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length)).ToList();

		Assert.Single(tags);
		Assert.Equal("fixed", tags[0].Tag.Name);
	}

	[Fact]
	public void Aggregator_Only_Sees_Providers_For_Matching_ContentType()
	{
		var provider = new FixedSpanTaggerProvider();
		using var registration = EditorCompositionHost.RegisterTaggerProvider("XML", provider);

		// The buffer's content type is "text", which is not "XML" nor a base type of it.
		var buffer = CreateBuffer("hello world");
		var factory = new AvalonTagAggregatorFactoryService();
		using var aggregator = factory.CreateTagAggregator<TestTag>(buffer);

		var tags = aggregator.GetTags(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length)).ToList();

		Assert.Empty(tags);
	}

	[Fact]
	public void Aggregator_Sees_Provider_Registered_For_A_Base_ContentType()
	{
		// "text" is a base type of "XAML" (text -> XML -> XAML), so a provider registered for
		// "text" applies to an XAML-content-typed buffer too (section 19's base-type chain).
		var provider = new FixedSpanTaggerProvider();
		using var registration = EditorCompositionHost.RegisterTaggerProvider("text", provider);

		var buffer = AvalonTextBufferRegistry.GetOrCreate(new TextDocument("<a/>"), AvalonContentTypeRegistry.Xaml);
		var factory = new AvalonTagAggregatorFactoryService();
		using var aggregator = factory.CreateTagAggregator<TestTag>(buffer);

		var tags = aggregator.GetTags(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length)).ToList();

		Assert.Single(tags);
	}

	[Fact]
	public void TagsChanged_Forwards_From_Underlying_Tagger()
	{
		var provider = new FixedSpanTaggerProvider();
		using var registration = EditorCompositionHost.RegisterTaggerProvider("text", provider);

		var buffer = CreateBuffer("hello world");
		var factory = new AvalonTagAggregatorFactoryService();
		using var aggregator = factory.CreateTagAggregator<TestTag>(buffer);

		SnapshotSpan? raised = null;
		aggregator.TagsChanged += (_, e) => raised = e.Span.GetSpans(buffer).Single();

		var span = new SnapshotSpan(buffer.CurrentSnapshot, 0, 3);
		provider.LastCreated.RaiseTagsChanged(span);

		Assert.Equal(span, raised);
	}

	[Fact]
	public void Unregistering_A_Provider_Removes_It_From_Future_Aggregators()
	{
		var provider = new FixedSpanTaggerProvider();
		var registration = EditorCompositionHost.RegisterTaggerProvider("inert", provider);
		registration.Dispose();

		var buffer = AvalonTextBufferRegistry.GetOrCreate(new TextDocument("x"), AvalonContentTypeRegistry.Inert);
		var factory = new AvalonTagAggregatorFactoryService();
		using var aggregator = factory.CreateTagAggregator<TestTag>(buffer);

		var tags = aggregator.GetTags(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length)).ToList();

		Assert.Empty(tags);
	}
}
