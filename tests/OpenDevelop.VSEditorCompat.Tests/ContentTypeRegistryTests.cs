// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IContentTypeRegistryService tests: the standard hierarchy resolves, IsOfType spans a family
// through base types, and content types can be added/removed dynamically (vs-editor-api.md
// section 19).

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class ContentTypeRegistryTests
{
	readonly AvalonContentTypeRegistryService registry = new();

	[Fact]
	public void Standard_Hierarchy_Is_Seeded()
	{
		Assert.Equal("any", registry.Any.TypeName);
		Assert.True(registry.Text.IsOfType("any"));
		Assert.True(registry.Code.IsOfType("text"));
		Assert.True(registry.CSharp.IsOfType("code"));
		Assert.True(registry.Xaml.IsOfType("XML"));
		Assert.Same(registry.GetContentType("CSharp"), registry.CSharp);
		Assert.Same(registry.GetContentType("unknown"), registry.UnknownContentType);
	}

	[Fact]
	public void IsOfType_Span_Is_Transitive_And_Case_Insensitive()
	{
		Assert.True(registry.FSharp.IsOfType("fsharp"));
		Assert.True(registry.Xaml.IsOfType("text"));
		Assert.False(registry.Xaml.IsOfType("code"));
		Assert.False(registry.Code.IsOfType("XAML"));
	}

	[Fact]
	public void AddContentType_Resolves_Base_Types_From_Registry()
	{
		var added = registry.AddContentType("JSON", new[] { "text" });
		Assert.Same(added, registry.GetContentType("JSON"));
		Assert.True(added.IsOfType("text"));
		Assert.True(added.IsOfType("any"));
	}

	[Fact]
	public void AddContentType_With_Unknown_Base_Throws()
	{
		Assert.Throws<ArgumentException>(() => registry.AddContentType("Bogus", new[] { "does-not-exist" }));
	}

	[Fact]
	public void RemoveContentType_Makes_It_Unresolvable()
	{
		registry.AddContentType("Temp", new[] { "text" });
		Assert.NotNull(registry.GetContentType("Temp"));
		registry.RemoveContentType("Temp");
		Assert.Null(registry.GetContentType("Temp"));
	}

	[Fact]
	public void ContentTypes_Lists_All_Registered_Types()
	{
		Assert.Contains(registry.ContentTypes, t => t.TypeName == "CSharp");
		Assert.Contains(registry.ContentTypes, t => t.TypeName == "XAML");
	}
}
