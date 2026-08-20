// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// A small, editor-only composition surface (vs-editor-api.md section 27) - deliberately NOT a
// second general OpenDevelop service container, and deliberately NOT a MEF catalog that scans
// assemblies for [Export]-attributed types yet ("Start with explicit service registration. Add
// metadata-driven MEF discovery only after the core editor interfaces work."). Extensions call
// RegisterTaggerProvider/RegisterClassifierProvider directly; AvalonTagAggregatorFactoryService
// and AvalonClassifierAggregatorService are the only readers.
//
// This host owns process-lifetime singletons - EditorServiceRegistry is its (per-kind) backing
// store, kept internal so callers only ever see the facade below.

using System;

using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public static class EditorCompositionHost
{
	static readonly AvalonTaggerProviderRegistry taggerProviders = new();
	static readonly AvalonClassifierProviderRegistry classifierProviders = new();

	public static IDisposable RegisterTaggerProvider(string contentTypeName, ITaggerProvider provider)
		=> taggerProviders.Register(contentTypeName, provider);

	public static System.Collections.Generic.IReadOnlyList<ITaggerProvider> GetTaggerProviders(IContentType contentType)
		=> taggerProviders.GetProviders(contentType);

	public static IDisposable RegisterClassifierProvider(string contentTypeName, IClassifierProvider provider)
		=> classifierProviders.Register(contentTypeName, provider);

	public static System.Collections.Generic.IReadOnlyList<IClassifierProvider> GetClassifierProviders(IContentType contentType)
		=> classifierProviders.GetProviders(contentType);
}
