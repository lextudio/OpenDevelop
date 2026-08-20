// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IClassifierAggregatorService: builds one AvalonClassifierAggregator per buffer from
// EditorCompositionHost's registered IClassifierProvider list for that buffer's content type.

using System.Collections.Generic;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonClassifierAggregatorService : IClassifierAggregatorService
{
	public IClassifier GetClassifier(ITextBuffer textBuffer)
	{
		var providers = EditorCompositionHost.GetClassifierProviders(textBuffer.ContentType);
		var classifiers = new List<IClassifier>();
		foreach (var provider in providers) {
			var classifier = provider.GetClassifier(textBuffer);
			if (classifier != null)
				classifiers.Add(classifier);
		}
		return new AvalonClassifierAggregator(classifiers);
	}
}
