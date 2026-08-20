// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Explicit ITaggerProvider registration by content type name (vs-editor-api.md section 27:
// "Start with explicit service registration. Add metadata-driven MEF discovery only after the
// core editor interfaces work."). EditorCompositionHost is the facade extensions use; this class
// is its backing store.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTaggerProviderRegistry
{
	readonly Dictionary<string, List<ITaggerProvider>> providersByContentType = new(StringComparer.OrdinalIgnoreCase);
	readonly object syncRoot = new();

	public IDisposable Register(string contentTypeName, ITaggerProvider provider)
	{
		if (string.IsNullOrEmpty(contentTypeName))
			throw new ArgumentException("A content type name is required.", nameof(contentTypeName));
		if (provider == null)
			throw new ArgumentNullException(nameof(provider));

		lock (syncRoot) {
			if (!providersByContentType.TryGetValue(contentTypeName, out var list)) {
				list = new List<ITaggerProvider>();
				providersByContentType[contentTypeName] = list;
			}
			list.Add(provider);
		}

		return new Unregister(this, contentTypeName, provider);
	}

	public IReadOnlyList<ITaggerProvider> GetProviders(IContentType contentType)
	{
		if (contentType == null)
			return Array.Empty<ITaggerProvider>();

		lock (syncRoot) {
			// A provider registered for "text" also applies to "CSharp" etc. - content types
			// form a base-type chain (vs-editor-api.md section 19), so walk it the same way
			// AvalonContentType.IsOfType does.
			var result = new List<ITaggerProvider>();
			foreach (var candidate in providersByContentType.Keys) {
				if (contentType.IsOfType(candidate))
					result.AddRange(providersByContentType[candidate]);
			}
			return result;
		}
	}

	sealed class Unregister : IDisposable
	{
		readonly AvalonTaggerProviderRegistry registry;
		readonly string contentTypeName;
		readonly ITaggerProvider provider;
		bool disposed;

		public Unregister(AvalonTaggerProviderRegistry registry, string contentTypeName, ITaggerProvider provider)
		{
			this.registry = registry;
			this.contentTypeName = contentTypeName;
			this.provider = provider;
		}

		public void Dispose()
		{
			if (disposed)
				return;
			disposed = true;
			lock (registry.syncRoot) {
				if (registry.providersByContentType.TryGetValue(contentTypeName, out var list))
					list.Remove(provider);
			}
		}
	}
}
