// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Convenience facade over a shared AvalonContentTypeRegistryService (the official
// Microsoft.VisualStudio.Utilities.IContentTypeRegistryService). The spike's early tests and the
// buffer factory use this instead of hand-rolling content types.

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>The shared content type registry and its standard hierarchy.</summary>
public static class AvalonContentTypeRegistry
{
	static readonly AvalonContentTypeRegistryService shared = new();

	/// <summary>The shared registry instance; pass this wherever an IContentTypeRegistryService is needed.</summary>
	public static AvalonContentTypeRegistryService Instance => shared;

	public static IContentType Any => shared.Any;
	public static IContentType Text => shared.Text;
	public static IContentType PlainText => shared.PlainText;
	public static IContentType Inert => shared.Inert;
	public static IContentType Code => shared.Code;
	public static IContentType CSharp => shared.CSharp;
	public static IContentType Basic => shared.Basic;
	public static IContentType FSharp => shared.FSharp;
	public static IContentType Xml => shared.Xml;
	public static IContentType Xaml => shared.Xaml;

	/// <summary>Gets a registered content type by name, or null when unknown.</summary>
	public static IContentType GetContentType(string typeName) => shared.GetContentType(typeName);
}
