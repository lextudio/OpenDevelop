// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// EditorCompositionHost tests (vs-editor-api.md section 27): explicit registration/lookup by
// content type, and that unregistering removes a provider from later lookups without disturbing
// providers registered under other content types.

using System.Linq;

using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class EditorCompositionHostTests
{
	sealed class NoOpTaggerProvider : ITaggerProvider
	{
		public ITagger<T> CreateTagger<T>(Microsoft.VisualStudio.Text.ITextBuffer buffer) where T : Microsoft.VisualStudio.Text.Tagging.ITag => null;
	}

	sealed class NoOpClassifierProvider : IClassifierProvider
	{
		public IClassifier GetClassifier(Microsoft.VisualStudio.Text.ITextBuffer textBuffer) => null;
	}

	[Fact]
	public void RegisterTaggerProvider_Makes_It_Discoverable_By_ContentType()
	{
		var contentType = AvalonContentTypeRegistry.GetContentType("CSharp");
		var provider = new NoOpTaggerProvider();
		using var registration = EditorCompositionHost.RegisterTaggerProvider("CSharp", provider);

		var found = EditorCompositionHost.GetTaggerProviders(contentType);

		Assert.Contains(provider, found);
	}

	[Fact]
	public void Disposing_The_Registration_Removes_The_Provider()
	{
		var contentType = AvalonContentTypeRegistry.GetContentType("Basic");
		var provider = new NoOpTaggerProvider();
		var registration = EditorCompositionHost.RegisterTaggerProvider("Basic", provider);

		registration.Dispose();

		Assert.DoesNotContain(provider, EditorCompositionHost.GetTaggerProviders(contentType));
	}

	[Fact]
	public void Unregistering_One_Provider_Does_Not_Affect_Others_On_The_Same_ContentType()
	{
		var contentType = AvalonContentTypeRegistry.GetContentType("FSharp");
		var providerA = new NoOpTaggerProvider();
		var providerB = new NoOpTaggerProvider();
		var registrationA = EditorCompositionHost.RegisterTaggerProvider("FSharp", providerA);
		using var registrationB = EditorCompositionHost.RegisterTaggerProvider("FSharp", providerB);

		registrationA.Dispose();

		var found = EditorCompositionHost.GetTaggerProviders(contentType);
		Assert.DoesNotContain(providerA, found);
		Assert.Contains(providerB, found);
	}

	[Fact]
	public void RegisterClassifierProvider_Makes_It_Discoverable_By_ContentType()
	{
		var contentType = AvalonContentTypeRegistry.Xml;
		var provider = new NoOpClassifierProvider();
		using var registration = EditorCompositionHost.RegisterClassifierProvider("XML", provider);

		var found = EditorCompositionHost.GetClassifierProviders(contentType);

		Assert.Contains(provider, found);
	}

	[Fact]
	public void GetTaggerProviders_Returns_Empty_For_Null_ContentType()
	{
		Assert.Empty(EditorCompositionHost.GetTaggerProviders(null));
	}
}
