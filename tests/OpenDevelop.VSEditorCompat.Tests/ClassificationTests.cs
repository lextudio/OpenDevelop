// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IClassifier/IClassifierProvider and IClassificationType(RegistryService) tests
// (vs-editor-api.md section 26).

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class ClassificationTests
{
	sealed class FixedClassifier : IClassifier
	{
		readonly IClassificationType type;

		public FixedClassifier(IClassificationType type) => this.type = type;

		public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

		public void RaiseClassificationChanged(SnapshotSpan span) => ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(span));

		public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
			=> new List<ClassificationSpan> { new(span, type) };
	}

	sealed class FixedClassifierProvider : IClassifierProvider
	{
		readonly IClassificationType type;
		public FixedClassifier LastCreated { get; private set; }

		public FixedClassifierProvider(IClassificationType type) => this.type = type;

		public IClassifier GetClassifier(ITextBuffer textBuffer)
		{
			var classifier = new FixedClassifier(type);
			LastCreated = classifier;
			return classifier;
		}
	}

	static AvalonTextBuffer CreateBuffer(string text)
		=> AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text), AvalonContentTypeRegistry.Text);

	[Fact]
	public void Registry_Seeds_The_Well_Known_Base_Types()
	{
		var registry = new AvalonClassificationTypeRegistryService();

		var comment = registry.GetClassificationType("comment");
		var stringType = registry.GetClassificationType("string");

		Assert.NotNull(comment);
		Assert.True(comment.IsOfType("natural language"));
		Assert.NotNull(stringType);
		Assert.True(stringType.IsOfType("literal"));
		Assert.True(stringType.IsOfType("formal language"));
	}

	[Fact]
	public void CreateClassificationType_Rejects_Duplicate_Names()
	{
		var registry = new AvalonClassificationTypeRegistryService();
		Assert.Throws<InvalidOperationException>(() => registry.CreateClassificationType("comment", Enumerable.Empty<IClassificationType>()));
	}

	[Fact]
	public void CreateTransientClassificationType_Does_Not_Register_By_Name()
	{
		var registry = new AvalonClassificationTypeRegistryService();
		var baseType = registry.GetClassificationType("keyword");

		var transient = registry.CreateTransientClassificationType(baseType);

		Assert.True(transient.IsOfType("keyword"));
		Assert.True(transient.IsOfType("formal language"));
	}

	[Fact]
	public void AggregatorService_Surfaces_Spans_From_Registered_Provider()
	{
		var registry = new AvalonClassificationTypeRegistryService();
		var keyword = registry.GetClassificationType("keyword");
		var provider = new FixedClassifierProvider(keyword);
		using var registration = EditorCompositionHost.RegisterClassifierProvider("text", provider);

		var buffer = CreateBuffer("class C {}");
		var classifier = new AvalonClassifierAggregatorService().GetClassifier(buffer);

		var spans = classifier.GetClassificationSpans(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length));

		Assert.Single(spans);
		Assert.Same(keyword, spans[0].ClassificationType);
	}

	[Fact]
	public void AggregatorService_Ignores_Providers_For_Other_ContentTypes()
	{
		var registry = new AvalonClassificationTypeRegistryService();
		var keyword = registry.GetClassificationType("keyword");
		var provider = new FixedClassifierProvider(keyword);
		using var registration = EditorCompositionHost.RegisterClassifierProvider("CSharp", provider);

		var buffer = CreateBuffer("class C {}"); // content type "text", not "CSharp"
		var classifier = new AvalonClassifierAggregatorService().GetClassifier(buffer);

		var spans = classifier.GetClassificationSpans(new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length));

		Assert.Empty(spans);
	}

	[Fact]
	public void ClassificationChanged_Forwards_From_Underlying_Classifier()
	{
		var registry = new AvalonClassificationTypeRegistryService();
		var keyword = registry.GetClassificationType("keyword");
		var provider = new FixedClassifierProvider(keyword);
		using var registration = EditorCompositionHost.RegisterClassifierProvider("text", provider);

		var buffer = CreateBuffer("class C {}");
		var classifier = new AvalonClassifierAggregatorService().GetClassifier(buffer);

		SnapshotSpan? raised = null;
		classifier.ClassificationChanged += (_, e) => raised = e.ChangeSpan;

		var span = new SnapshotSpan(buffer.CurrentSnapshot, 0, 5);
		provider.LastCreated.RaiseClassificationChanged(span);

		Assert.Equal(span, raised);
	}
}
