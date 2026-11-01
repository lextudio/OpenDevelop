using ICSharpCode.GtkDesigner;
using ICSharpCode.SharpDevelop.LanguageServices;
using Xunit;

namespace GtkDesigner.Tests;

/// <summary>The in-process GtkBuilder language service: completion from the installed GIR,
/// diagnostics from the same rules the designer enforces, outline, id navigation and rename.
/// Tests that need GIR data skip when no Gtk-4.0.gir is installed.</summary>
public sealed class GtkUiLanguageServiceTests
{
	static readonly DocumentId id = new("test.ui");

	static (string Text, int Offset) At(string text)
	{
		var offset = text.IndexOf('|');
		Assert.True(offset >= 0, "The fixture needs a '|' caret marker.");
		return (text.Remove(offset, 1), offset);
	}

	static GtkUiLanguageService Service(string text)
	{
		var service = new GtkUiLanguageService();
		service.UpsertDocumentAsync(id, text, CancellationToken.None).GetAwaiter().GetResult();
		return service;
	}

	static List<CompletionItem> Complete(string marked)
	{
		var (text, offset) = At(marked);
		return Service(text).GetCompletionsAsync(id, offset, CancellationToken.None).GetAwaiter().GetResult().Items.ToList();
	}

	static IReadOnlyList<LanguageDiagnostic> Diagnose(string text)
		=> Service(text).GetDiagnosticsAsync(id, CancellationToken.None).GetAwaiter().GetResult();

	[Fact]
	public void Parse_KeepsSpansAndToleratesAnUnfinishedTag()
	{
		var document = GtkUiDocument.Parse("<interface><object class=\"GtkButton\"");
		var interfaceElement = Assert.Single(document.Roots);
		var button = Assert.Single(interfaceElement.Children);
		Assert.Equal("object", button.Name);
		Assert.True(button.Incomplete);
		var classAttribute = button.Attribute("class");
		Assert.NotNull(classAttribute);
		Assert.Equal("GtkButton", classAttribute!.Value);
		Assert.Equal("GtkButton", document.Text.Substring(classAttribute.ValueStart, classAttribute.ValueEnd - classAttribute.ValueStart));
		Assert.Equal(button.NameStart, document.Text.IndexOf("object", StringComparison.Ordinal));
	}

	[Fact]
	public void Parse_ReportsUnclosedElementsWithTheirOffset()
	{
		var document = GtkUiDocument.Parse("<interface><object class=\"GtkLabel\"></interface>");
		var error = Assert.Single(document.Errors);
		Assert.Contains("<object>", error.Message);
		Assert.Equal(11, error.Offset);
	}

	[Fact]
	public void Completion_OffersGtkAndAdwClassesInsideAClassAttribute()
	{
		GirCatalogTests.LoadedCatalog();
		var items = Complete("<interface><object class=\"GtkBu|\"/></interface>");
		Assert.Contains(items, item => item.DisplayText == "GtkButton" && item.InsertionText == "GtkButton");
		Assert.DoesNotContain(items, item => item.DisplayText == "GtkLabel");
	}

	[Fact]
	public void Completion_OffersPropertiesAndInsertsOnlyTheWordAfterAHyphen()
	{
		GirCatalogTests.LoadedCatalog();
		var items = Complete("<interface><object class=\"GtkWindow\"><property name=\"default-wid|\"/></object></interface>");
		var width = Assert.Single(items, item => item.DisplayText == "default-width");
		// The editor replaces the word before the caret ("wid"), so the insertion is the tail only.
		Assert.Equal("width", width.InsertionText);
	}

	[Fact]
	public void Completion_OffersEnumMembersInAPropertyValue()
	{
		GirCatalogTests.LoadedCatalog();
		var items = Complete("<interface><object class=\"GtkBox\"><property name=\"orientation\">hor|</property></object></interface>");
		Assert.Contains(items, item => item.DisplayText == "horizontal");
	}

	[Fact]
	public void Completion_OffersSignalsAndBuildableChildSlots()
	{
		GirCatalogTests.LoadedCatalog();
		var signals = Complete("<interface><object class=\"GtkButton\"><signal name=\"clic|\" handler=\"on_click\"/></object></interface>");
		Assert.Contains(signals, item => item.DisplayText == "clicked");

		var slots = Complete("<interface><object class=\"GtkHeaderBar\"><child type=\"st|\"><object class=\"GtkLabel\"/></child></object></interface>");
		Assert.Contains(slots, item => item.DisplayText == "start");
		Assert.DoesNotContain(slots, item => item.DisplayText == "tab");
	}

	[Fact]
	public void Completion_OffersObjectIdsForAReferenceProperty()
	{
		var items = Complete("""
			<interface>
			  <object class="GtkWindow" id="win"><property name="child">lab|</property></object>
			  <object class="GtkLabel" id="label1"/>
			</interface>
			""");
		Assert.Contains(items, item => item.DisplayText == "label1" && item.Description == "GtkLabel");
		Assert.DoesNotContain(items, item => item.DisplayText == "win");
	}

	[Fact]
	public void Completion_OffersElementNamesAndAttributeNamesInsideATag()
	{
		var elements = Complete("<interface><obj|/></interface>");
		Assert.Contains(elements, item => item.DisplayText == "object");

		var attributes = Complete("<interface><object class=\"GtkLabel\"><property name=\"label\" transla|/></object></interface>");
		Assert.Contains(attributes, item => item.DisplayText == "translatable");
	}

	[Fact]
	public void Diagnostics_ReportUnknownClassPropertySignalDuplicateIdAndDanglingReference()
	{
		GirCatalogTests.LoadedCatalog();
		var diagnostics = Diagnose("""
			<interface>
			  <requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="win">
			    <property name="nonsense">1</property>
			    <property name="child">missing</property>
			    <signal name="nonsense" handler="on_nonsense"/>
			  </object>
			  <object class="GtkNonsense" id="win"/>
			</interface>
			""");
		Assert.Contains(diagnostics, d => d.Id == "GTK1004"); // duplicate id
		Assert.Contains(diagnostics, d => d.Id == "GTK1006" && d.Message.Contains("GtkNonsense"));
		Assert.Contains(diagnostics, d => d.Id == "GTK1014" && d.Message.Contains("nonsense"));
		Assert.Contains(diagnostics, d => d.Id == "GTK1016" && d.Message.Contains("missing"));
		Assert.Contains(diagnostics, d => d.Id == "GTK1017" && d.Message.Contains("nonsense"));
	}

	[Fact]
	public void Diagnostics_EnforceSingleChildPanedColumnAndSlotRules()
	{
		var diagnostics = Diagnose("""
			<interface>
			  <requires lib="gtk" version="4.0"/>
			  <object class="GtkFrame">
			    <child><object class="GtkLabel"/></child>
			    <child><object class="GtkLabel"/></child>
			  </object>
			  <object class="GtkHeaderBar">
			    <child type="middle"><object class="GtkLabel"/></child>
			  </object>
			  <object class="GtkColumnView">
			    <child><object class="GtkLabel"/></child>
			  </object>
			</interface>
			""");
		Assert.Contains(diagnostics, d => d.Id == "GTK1011" && d.Message.Contains("GtkFrame"));
		Assert.Contains(diagnostics, d => d.Id == "GTK1019" && d.Message.Contains("middle"));
		Assert.Contains(diagnostics, d => d.Id == "GTK1012");
	}

	[Fact]
	public void Diagnostics_ReportAnInvalidEnumValue()
	{
		GirCatalogTests.LoadedCatalog();
		var diagnostics = Diagnose("""
			<interface>
			  <requires lib="gtk" version="4.0"/>
			  <object class="GtkBox"><property name="orientation">sideways</property></object>
			</interface>
			""");
		Assert.Contains(diagnostics, d => d.Id == "GTK1015" && d.Message.Contains("sideways"));
	}

	[Fact]
	public void Diagnostics_AskForRequiresAndLibadwaita()
	{
		GirCatalogTests.LoadedCatalog();
		var missing = Diagnose("<interface><object class=\"GtkLabel\"/></interface>");
		Assert.Contains(missing, d => d.Id == "GTK1003");

		var adw = Diagnose("<interface><requires lib=\"gtk\" version=\"4.0\"/><object class=\"AdwWindow\"/></interface>");
		Assert.Contains(adw, d => d.Id == "GTK1009");
	}

	[Fact]
	public void Outline_ListsObjectsByIdAndClass()
	{
		var outline = Service("""
			<interface>
			  <object class="GtkWindow" id="win">
			    <property name="child"><object class="GtkBox" id="box"/></property>
			  </object>
			</interface>
			""").GetDocumentOutlineAsync(id, CancellationToken.None).GetAwaiter().GetResult();
		var window = Assert.Single(outline);
		Assert.Equal("win", window.Name);
		Assert.Equal("GtkWindow", window.Kind);
		Assert.Equal("box", Assert.Single(window.Children).Name);
	}

	[Fact]
	public void GoToDefinition_ResolvesAReferenceToTheIdDeclaration()
	{
		var text = """
			<interface>
			  <object class="GtkWindow" id="win"><property name="child">label1</property></object>
			  <object class="GtkLabel" id="label1"/>
			</interface>
			""";
		var service = Service(text);
		var offset = text.IndexOf("label1</property>", StringComparison.Ordinal) + 2;
		var targets = service.GoToDefinitionAsync(id, offset, CancellationToken.None).GetAwaiter().GetResult();
		var target = Assert.Single(targets);
		Assert.Equal("test.ui", target.FileName);
		var declaration = text.IndexOf("id=\"label1\"", StringComparison.Ordinal) + 4;
		Assert.Equal(text.Take(declaration).Count(c => c == '\n') + 1, target.Position.Line);
	}

	[Fact]
	public void References_CountLookupNamesButNotMenuLinkNames()
	{
		var lookupText = """
			<interface>
			  <object class="GtkLabel" id="label1"/>
			  <object class="GtkLabel"><property name="label"><lookup name="label1"/></property></object>
			</interface>
			""";
		var lookup = Service(lookupText);
		var lookupAt = lookupText.IndexOf("id=\"label1\"", StringComparison.Ordinal) + 4;
		var at = lookup.GetSymbolNameAsync(id, lookupAt, CancellationToken.None).GetAwaiter().GetResult();
		Assert.Equal("label1", at);
		var references = lookup.FindReferencesAsync(id, lookupAt, CancellationToken.None).GetAwaiter().GetResult();
		Assert.Equal(2, references!.References.Count);

		// <link name="section"> names a GMenu link, not an object, so it is not an id reference.
		var menuText = """
			<interface>
			  <object class="GtkLabel" id="section"/>
			  <menu id="menu1"><item><link name="section"><item><attribute name="label">A</attribute></item></link></item></menu>
			</interface>
			""";
		var menu = Service(menuText);
		var sectionAt = menuText.IndexOf("id=\"section\"", StringComparison.Ordinal) + 4;
		var declaration = menu.FindReferencesAsync(id, sectionAt, CancellationToken.None).GetAwaiter().GetResult();
		Assert.Single(declaration!.References);
	}

	[Fact]
	public void Rename_ReplacesEveryOccurrenceOfAnId()
	{
		var text = """
			<interface>
			  <object class="GtkWindow" id="win"><property name="child">label1</property></object>
			  <object class="GtkLabel" id="label1"/>
			</interface>
			""";
		var service = Service(text);
		var offset = text.IndexOf("label1</property>", StringComparison.Ordinal) + 2;
		var edits = service.RenameSymbolAsync(id, offset, "greeting", CancellationToken.None).GetAwaiter().GetResult();
		var fileEdits = Assert.Single(edits).Value;
		Assert.Equal(2, fileEdits.Count);
		Assert.All(fileEdits, edit => Assert.Equal("greeting", edit.NewText));

		var renamed = text;
		foreach (var edit in fileEdits.OrderByDescending(edit => edit.Span.Start.Line).ThenByDescending(edit => edit.Span.Start.Column))
			renamed = renamed.Remove(OffsetOf(renamed, edit.Span.Start), edit.Span.End.Column - edit.Span.Start.Column).Insert(OffsetOf(renamed, edit.Span.Start), edit.NewText);
		Assert.Contains("id=\"greeting\"", renamed);
		Assert.Contains(">greeting<", renamed);
	}

	[Fact]
	public void Rename_ToAnIdAnotherObjectOwns_IsRefused()
	{
		var text = """
			<interface>
			  <object class="GtkWindow" id="win"><property name="child">label1</property></object>
			  <object class="GtkLabel" id="label1"/>
			  <object class="GtkLabel" id="label2"/>
			</interface>
			""";
		var service = Service(text);
		var offset = text.IndexOf("label1</property>", StringComparison.Ordinal) + 2;
		Assert.Empty(service.RenameSymbolAsync(id, offset, "label2", CancellationToken.None).GetAwaiter().GetResult());
	}

	[Fact]
	public void CloseDocument_ForgetsEveryDocumentOfThatFile_AndNoOther()
	{
		var service = new GtkUiLanguageService();
		var open = new DocumentId("/tmp/open.ui");
		var closed = new DocumentId("/tmp/closed.ui");
		service.UpsertDocumentAsync(open, "<interface/>", CancellationToken.None).GetAwaiter().GetResult();
		service.UpsertDocumentAsync(closed, "<interface/>", CancellationToken.None).GetAwaiter().GetResult();
		service.CloseDocumentAsync("/tmp/closed.ui").GetAwaiter().GetResult();
		Assert.Equal(DocumentReadiness.Unknown, service.GetDocumentReadiness(closed));
		Assert.Equal(DocumentReadiness.Ready, service.GetDocumentReadiness(open));
		service.CloseDocumentAsync("/tmp/never-seen.ui").GetAwaiter().GetResult();   // not an error
	}

	static int OffsetOf(string text, TextPosition position)
	{
		var line = 1;
		var offset = 0;
		while (offset < text.Length && line < position.Line) {
			if (text[offset] == '\n')
				line++;
			offset++;
		}
		return offset + position.Column - 1;
	}

	[Fact]
	public void QuickInfo_DescribesAClassAndAProperty()
	{
		GirCatalogTests.LoadedCatalog();
		var text = "<interface><object class=\"GtkButton\" id=\"button\"/></interface>";
		var service = Service(text);
		var classInfo = service.GetQuickInfoAsync(id, text.IndexOf("GtkButton", StringComparison.Ordinal) + 2, CancellationToken.None).GetAwaiter().GetResult();
		Assert.NotNull(classInfo);
		Assert.Contains("GtkButton : GtkWidget", classInfo!.Text);

		var propertyText = "<interface><object class=\"GtkLabel\"><property name=\"label\">Hi</property></object></interface>";
		var propertyService = Service(propertyText);
		var propertyInfo = propertyService.GetQuickInfoAsync(id, propertyText.IndexOf("\"label\"", StringComparison.Ordinal) + 2, CancellationToken.None).GetAwaiter().GetResult();
		Assert.NotNull(propertyInfo);
		Assert.Contains("label: utf8", propertyInfo!.Text);
		Assert.Contains("GtkLabel", propertyInfo.Text);
	}

	[Fact]
	public void IsValidIdentifier_AcceptsNcNamesOnly()
	{
		var service = Service("<interface/>");
		Assert.True(service.IsValidIdentifierAsync(id, "label1", CancellationToken.None).GetAwaiter().GetResult());
		Assert.True(service.IsValidIdentifierAsync(id, "default-width", CancellationToken.None).GetAwaiter().GetResult());
		Assert.False(service.IsValidIdentifierAsync(id, "1label", CancellationToken.None).GetAwaiter().GetResult());
		Assert.False(service.IsValidIdentifierAsync(id, "has space", CancellationToken.None).GetAwaiter().GetResult());
	}

	[Fact]
	public void TextChanges_UpdateTheStoredDocument()
	{
		var service = new GtkUiLanguageService();
		var text = "<interface><object class=\"GtkLabel\" id=\"a\"/></interface>";
		service.UpsertDocumentAsync(id, text, CancellationToken.None).GetAwaiter().GetResult();
		var value = text.IndexOf("\"a\"", StringComparison.Ordinal) + 1;
		service.OnTextChanged(id, new TextChange(new TextSpan(new TextPosition(1, value + 1), new TextPosition(1, value + 2)), "b"));
		var outline = service.GetDocumentOutlineAsync(id, CancellationToken.None).GetAwaiter().GetResult();
		Assert.Equal("b", Assert.Single(outline).Name);
	}
}
