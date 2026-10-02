using System.Runtime.InteropServices;
using ICSharpCode.GtkDesigner;
using Xunit;

namespace GtkDesigner.Tests;

/// <summary>The GIR catalogue against the GTK 4 introspection data actually installed - the same
/// files the design host reads. Skipped where no Gtk-4.0.gir is installed.</summary>
public sealed class GirCatalogTests
{
	static readonly Lazy<GirCatalog?> catalog = new(() => FindGirDirectory() is { } dir ? GirCatalog.Load(dir) : null);

	static string? FindGirDirectory()
	{
		var candidates = new List<string?> { Environment.GetEnvironmentVariable("GTK4_GIR_DIR") };
		if (OperatingSystem.IsWindows()) {
			var env = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? new[] { "clangarm64" } : new[] { "ucrt64", "mingw64", "clang64" };
			candidates.AddRange(env.Select(e => Path.Combine(@"C:\msys64", e, "share", "gir-1.0")));
		}
		candidates.AddRange(new[] { "/opt/homebrew/share/gir-1.0", "/usr/local/share/gir-1.0", "/usr/share/gir-1.0" });
		return candidates.FirstOrDefault(d => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "Gtk-4.0.gir")));
	}

	static GirCatalog Catalog() => LoadedCatalog();

	/// <summary>The shared catalogue for GIR-backed tests; skips the test when none is installed.</summary>
	internal static GirCatalog LoadedCatalog()
	{
		Assert.SkipWhen(catalog.Value == null, "GTK 4 introspection data (Gtk-4.0.gir) is not installed.");
		return catalog.Value!;
	}

	[Fact]
	public void Box_InheritsWidgetAndOrientableProperties_MostDerivedFirst()
	{
		var properties = Catalog().PropertiesOf("GtkBox");
		var names = properties.Select(p => p.Name).ToList();
		Assert.Contains("spacing", names);        // GtkBox's own
		Assert.Contains("halign", names);         // from GtkWidget
		Assert.Contains("orientation", names);    // from the GtkOrientable interface
		Assert.True(names.IndexOf("spacing") < names.IndexOf("halign"), "the class's own properties come before inherited ones");
		Assert.Equal(names.Count, names.Distinct().Count());
	}

	[Fact]
	public void EnumProperty_HasMembersAndNickDefault()
	{
		var halign = Catalog().FindProperty("GtkButton", "halign")!;
		Assert.Equal(GirValueKind.Enum, halign.Kind);
		Assert.Equal("fill", halign.DefaultValue);   // GIR says GTK_ALIGN_FILL; GtkBuilder writes the nick
		Assert.Contains(halign.Enum!.Members, m => m.Nick == "center");
		Assert.Equal("GtkWidget", Catalog().FindByQualifiedName(halign.DeclaringType)!.TypeName);
	}

	[Fact]
	public void ScalarKinds_AndBooleanDefault_AreTyped()
	{
		var cat = Catalog();
		Assert.Equal(GirValueKind.Integer, cat.FindProperty("GtkBox", "spacing")!.Kind);
		Assert.Equal(GirValueKind.String, cat.FindProperty("GtkButton", "label")!.Kind);
		var sensitive = cat.FindProperty("GtkButton", "sensitive")!;
		Assert.Equal(GirValueKind.Boolean, sensitive.Kind);
		Assert.Equal("True", sensitive.DefaultValue);
		Assert.Equal(GirValueKind.Double, cat.FindProperty("GtkWidget", "opacity")!.Kind);
		// The underscore spelling GtkBuilder also accepts finds the same property.
		Assert.Equal("margin-start", cat.FindProperty("GtkButton", "margin_start")!.Name);
	}

	[Fact]
	public void Libadwaita_JoinsTheCatalogue_WhenItsGirIsInstalled()
	{
		var dir = FindGirDirectory();
		Assert.SkipWhen(dir == null || !File.Exists(Path.Combine(dir, "Adw-1.gir")), "libadwaita introspection data (Adw-1.gir) is not installed.");
		var both = GirCatalog.Load(dir!, new[] { "Gtk-4.0", "Adw-1" });
		var statusPage = both.FindByTypeName("AdwStatusPage");
		Assert.NotNull(statusPage);
		Assert.Equal("Adw", GirCatalog.NamespaceOf(statusPage!));
		Assert.Equal(GirValueKind.String, both.FindProperty("AdwStatusPage", "title")!.Kind);
		Assert.NotNull(both.FindProperty("AdwStatusPage", "halign"));   // inherited from GtkWidget across namespaces
		Assert.Null(Catalog().FindByTypeName("AdwStatusPage"));          // the GTK-only catalogue does not assume it
	}

	[Fact]
	public void PropertyHeldObject_IsATreeChild_NotATextValue()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""
			<interface><requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="w"><property name="title">T</property>
			    <property name="child"><object class="GtkLabel" id="inside"/></property>
			  </object>
			</interface>
			"""), editor.Error);
		var window = editor.Roots[0];
		Assert.Equal(new[] { "inside" }, window.Children.Select(c => c.Id));
		Assert.False(window.Properties.ContainsKey("child"));
		Assert.Equal("T", window.Properties["title"]);
	}

	[Fact]
	public void Signals_IncludeInheritedOnes()
	{
		var signals = Catalog().SignalsOf("GtkButton").Select(s => s.Name).ToList();
		Assert.Contains("clicked", signals);
		Assert.Contains("realize", signals);   // GtkWidget's
		Assert.True(Catalog().IsA("GtkApplicationWindow", "GtkWindow"));
	}

	[Theory]
	[InlineData("spacing", "12", true, "12")]
	[InlineData("spacing", " 4 ", true, "4")]
	[InlineData("spacing", "wide", false, "")]
	[InlineData("halign", "CENTER", true, "center")]
	[InlineData("halign", "GTK_ALIGN_END", true, "end")]
	[InlineData("halign", "middle", false, "")]
	[InlineData("sensitive", "yes", true, "True")]
	[InlineData("sensitive", "0", true, "False")]
	[InlineData("sensitive", "maybe", false, "")]
	[InlineData("opacity", "0.5", true, "0.5")]
	public void TryNormalizeValue_CanonicalisesOrRejects(string property, string input, bool ok, string expected)
	{
		var p = Catalog().FindProperty("GtkBox", property)!;
		Assert.Equal(ok, GirCatalog.TryNormalizeValue(p, input, out var normalized, out var error));
		if (ok) Assert.Equal(expected, normalized);
		else Assert.False(string.IsNullOrEmpty(error));
	}
}
