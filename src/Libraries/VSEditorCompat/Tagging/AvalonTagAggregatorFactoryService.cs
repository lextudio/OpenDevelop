// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITagAggregatorFactoryService: creates each buffer's taggers from EditorCompositionHost's
// registered ITaggerProvider list for the buffer's content type (vs-editor-api.md section 25).

using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTagAggregatorFactoryService : IViewTagAggregatorFactoryService, IBufferTagAggregatorFactoryService
{
	public ITagAggregator<T> CreateTagAggregator<T>(ITextBuffer textBuffer) where T : ITag
	{
		var providers = EditorCompositionHost.GetTaggerProviders(textBuffer.ContentType);
		var taggers = new List<ITagger<T>>();
		foreach (var provider in providers) {
			var tagger = provider.CreateTagger<T>(textBuffer);
			if (tagger != null)
				taggers.Add(tagger);
		}
		return new AvalonTagAggregator<T>(textBuffer, taggers);
	}

	public ITagAggregator<T> CreateTagAggregator<T>(ITextBuffer textBuffer, TagAggregatorOptions options) where T : ITag
		=> CreateTagAggregator<T>(textBuffer);

	public ITagAggregator<T> CreateTagAggregator<T>(ITextView textView) where T : ITag
		=> CreateTagAggregator<T>(textView.TextBuffer);

	public ITagAggregator<T> CreateTagAggregator<T>(ITextView textView, TagAggregatorOptions options) where T : ITag
		=> CreateTagAggregator<T>(textView.TextBuffer);
}
