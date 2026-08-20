// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Explicit IClassifierProvider registration by content type name - the classifier counterpart of
// AvalonTaggerProviderRegistry (same "explicit first, MEF later" rule, section 27).

using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonClassifierProviderRegistry
{
	readonly Dictionary<string, List<IClassifierProvider>> providersByContentType = new(StringComparer.OrdinalIgnoreCase);
	readonly object syncRoot = new();

	public IDisposable Register(string contentTypeName, IClassifierProvider provider)
	{
		if (string.IsNullOrEmpty(contentTypeName))
			throw new ArgumentException("A content type name is required.", nameof(contentTypeName));
		if (provider == null)
			throw new ArgumentNullException(nameof(provider));

		lock (syncRoot) {
			if (!providersByContentType.TryGetValue(contentTypeName, out var list)) {
				list = new List<IClassifierProvider>();
				providersByContentType[contentTypeName] = list;
			}
			list.Add(provider);
		}

		return new Unregister(this, contentTypeName, provider);
	}

	public IReadOnlyList<IClassifierProvider> GetProviders(IContentType contentType)
	{
		if (contentType == null)
			return Array.Empty<IClassifierProvider>();

		lock (syncRoot) {
			var result = new List<IClassifierProvider>();
			foreach (var candidate in providersByContentType.Keys) {
				if (contentType.IsOfType(candidate))
					result.AddRange(providersByContentType[candidate]);
			}
			return result;
		}
	}

	sealed class Unregister : IDisposable
	{
		readonly AvalonClassifierProviderRegistry registry;
		readonly string contentTypeName;
		readonly IClassifierProvider provider;
		bool disposed;

		public Unregister(AvalonClassifierProviderRegistry registry, string contentTypeName, IClassifierProvider provider)
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
