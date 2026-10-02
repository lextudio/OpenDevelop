using ICSharpCode.GtkDesigner;
using Xunit;

namespace GtkDesigner.Tests;

/// <summary>Handler signatures and companion wiring for .ui &lt;signal&gt;s, against the installed
/// GTK 4 introspection data. (The generated code was also compiled and run against Gir.Core 0.8.1:
/// BuildUi loads a .ui whose signals made a plain GtkBuilder abort, and the handlers fire.)</summary>
public sealed class GtkSignalWiringTests
{
	const string Ui = """
		<interface>
		  <requires lib="gtk" version="4.0"/>
		  <object class="GtkWindow" id="window">
		    <signal name="close-request" handler="window_close_request"/>
		    <child><object class="GtkBox" id="box">
		      <child><object class="GtkButton" id="run">
		        <signal name="clicked" handler="run_clicked"/>
		        <signal name="notify::label" handler="run_label_changed"/></object></child>
		      <child><object class="GtkEntry" id="entry">
		        <signal name="insert-text" handler="entry_insert_text"/>
		        <signal name="changed" handler="entry_changed"/></object></child>
		      <child><object class="GtkButton">
		        <signal name="clicked" handler="anonymous_clicked"/></object></child>
		    </object></child>
		  </object>
		</interface>
		""";

	static IReadOnlyList<GtkSignalHandler> Handlers()
	{
		var catalog = GirCatalogTests.LoadedCatalog();
		return GtkSignalWiring.Handlers(Ui, catalog);
	}

	[Fact]
	public void Signatures_AreTypedFromGir()
	{
		var h = Handlers().ToDictionary(x => x.Handler);
		Assert.Equal("void run_clicked(Gtk.Button sender, System.EventArgs args)", h["run_clicked"].Signature);
		Assert.Equal("OnClicked", h["run_clicked"].EventName);
		// A returning signal returns GTK's "not handled" by default.
		Assert.Equal("bool window_close_request(Gtk.Window sender, System.EventArgs args)", h["window_close_request"].Signature);
		Assert.Equal("return false;", h["window_close_request"].DefaultBody);
		// A signal with parameters gets the SignalArgs class of the type that declares it (here the
		// GtkEditable interface), while the sender stays the object's own class.
		Assert.Equal("void entry_insert_text(Gtk.Entry sender, Gtk.Editable.InsertTextSignalArgs args)", h["entry_insert_text"].Signature);
		Assert.Equal("void entry_changed(Gtk.Entry sender, System.EventArgs args)", h["entry_changed"].Signature);
	}

	[Fact]
	public void UnsupportedSignals_AreReportedNotGuessed()
	{
		var h = Handlers().ToDictionary(x => x.Handler);
		Assert.False(h["run_label_changed"].Supported);
		Assert.Contains("detailed", h["run_label_changed"].Reason);
		Assert.False(h["anonymous_clicked"].Supported);
		Assert.Contains("no id", h["anonymous_clicked"].Reason);
	}

	[Fact]
	public void Companion_ConnectsSupportedSignals_AndExplainsTheRest()
	{
		var source = GtkSignalWiring.GenerateCompanion("Main.ui", "Demo", "MainWindow", Handlers());
		Assert.Contains("namespace Demo", source);
		Assert.Contains("partial class MainWindow", source);
		Assert.Contains("public global::Gtk.Builder BuildUi(string uiPath)", source);
		Assert.Contains("var @run = (global::Gtk.Button)(builder.GetObject(\"run\")", source);
		Assert.Contains("@run.OnClicked += (_, args) => run_clicked(@run, args);", source);
		Assert.Contains("@window.OnCloseRequest += (_, args) => window_close_request(@window, args);", source);
		Assert.Contains("@entry.OnInsertText += (_, args) => entry_insert_text(@entry, args);", source);
		Assert.Contains("// <signal name=\"notify::label\" handler=\"run_label_changed\"> on 'run' is not connected: a detailed signal", source);
		Assert.DoesNotContain("anonymous_clicked(", source);
		// No namespace: the class is emitted at top level.
		Assert.DoesNotContain("namespace", GtkSignalWiring.GenerateCompanion("Main.ui", null, "MainWindow", Handlers()));
	}
}
