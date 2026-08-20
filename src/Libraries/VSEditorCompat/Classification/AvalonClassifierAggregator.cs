// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IClassifier that fans a query out to every classifier registered for the buffer's content type
// and concatenates the results (vs-editor-api.md section 26). Overlap resolution beyond simple
// concatenation (e.g. priority ordering between classifiers) is left to callers for now - not
// needed until a second real classifier provider exists to conflict with the first.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonClassifierAggregator : IClassifier
{
	readonly List<IClassifier> classifiers;

	public AvalonClassifierAggregator(IEnumerable<IClassifier> classifiers)
	{
		this.classifiers = classifiers?.ToList() ?? throw new ArgumentNullException(nameof(classifiers));
		foreach (var classifier in this.classifiers)
			classifier.ClassificationChanged += OnClassificationChanged;
	}

	public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

	void OnClassificationChanged(object sender, ClassificationChangedEventArgs e) => ClassificationChanged?.Invoke(this, e);

	public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
	{
		var result = new List<ClassificationSpan>();
		foreach (var classifier in classifiers)
			result.AddRange(classifier.GetClassificationSpans(span));
		return result;
	}
}
