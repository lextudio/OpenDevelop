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

	const string MenuUi = """
		<interface>
		  <requires lib="gtk" version="4.0"/>
		  <object class="GtkPopoverMenu" id="menu">
		    <property name="menu-model">
		      <menu>
		        <section>
		          <item><attribute name="label">Save</attribute><attribute name="action">win.save</attribute></item>
		          <item><attribute name="label">Save as</attribute><attribute name="action">win.save-as</attribute></item>
		          <item><attribute name="label">Again</attribute><attribute name="action">win.save</attribute></item>
		          <item><attribute name="label">Dotted</attribute><attribute name="action">win.save.as</attribute></item>
		          <item><attribute name="label">Underscore</attribute><attribute name="action">win.under_score</attribute></item>
		        </section>
		        <item><attribute name="label">Quit</attribute><attribute name="action">app.quit</attribute></item>
		        <item><attribute name="label">Odd</attribute><attribute name="action">win.bad name</attribute></item>
		        <item><attribute name="label">Plain</attribute></item>
		      </menu>
		    </property>
		  </object>
		</interface>
		""";

	[Fact]
	public void MenuActions_AreTheDistinctWellFormedWindowActions()
	{
		var actions = GtkSignalWiring.MenuActions(MenuUi);
		// "under_score" is not a GAction name (g_simple_action_new would return NULL), so it is left out.
		Assert.Equal(new[] { "action:save", "action:save-as", "action:save.as", "application-action:quit" }, actions.Select(a => a.ObjectId));
		// '-' and '.' map differently, so "save-as" and "save.as" are different methods without any suffix.
		Assert.Equal(new[] { "save_action", "save_as_action", "save__as_action", "app_quit_action" }, actions.Select(a => a.Handler));
		Assert.Equal("void save_as_action(Gio.SimpleAction sender, Gio.SimpleAction.ActivateSignalArgs args)", actions[1].Signature);
		Assert.Empty(GtkSignalWiring.MenuActions("<not xml"));
		// A leading digit is a valid action name but not a C# identifier start.
		var digit = GtkSignalWiring.MenuActions(MenuUi.Replace("win.save-as", "win.2nd"));
		Assert.Contains("_2nd_action", digit.Select(a => a.Handler));
		Assert.All(digit, a => Assert.True(GtkSignalWiring.IsIdentifier(a.Handler)));
	}

	[Fact]
	public void MenuActions_AnActionKeepsItsMethodWhenOthersAreAddedRemovedOrReordered()
	{
		string MethodOf(string menuNames, string name) => GtkSignalWiring.MenuActions(
			"<interface><object class=\"GtkPopoverMenu\" id=\"m\"><property name=\"menu-model\"><menu>" + string.Concat(menuNames.Split(',').Select(n => $"<item><attribute name=\"action\">win.{n}</attribute></item>"))
			+ "</menu></property></object></interface>").First(a => a.ObjectId == "action:" + name).Handler;
		// Adding or removing "save.as" next to "save-as" does not re-point either at another stub.
		Assert.Equal(MethodOf("save-as", "save-as"), MethodOf("save.as,save-as", "save-as"));
		Assert.Equal(MethodOf("save.as", "save.as"), MethodOf("save-as,save.as", "save.as"));
		Assert.NotEqual(MethodOf("save-as,save.as", "save-as"), MethodOf("save-as,save.as", "save.as"));
		// A pathological pair that still collides ("a--b" and "a.b" are both a__b) is told apart by ordinal name order, not menu order.
		Assert.Equal(MethodOf("a--b,a.b", "a.b"), MethodOf("a.b,a--b", "a.b"));
		Assert.NotEqual(MethodOf("a--b,a.b", "a--b"), MethodOf("a--b,a.b", "a.b"));
	}

		[Fact]
	public void Companion_RegistersWindowActionsAndLeavesAnActionlessCompanionUnchanged()
	{
		var source = GtkSignalWiring.GenerateCompanion("Main.ui", "Demo", "MainWindow", GtkSignalWiring.MenuActions(MenuUi));
		Assert.Contains("public void AddMenuActions(global::Gio.ActionMap map)", source);
		Assert.Contains("var @action_save_action = global::Gio.SimpleAction.New(\"save\", null);", source);
		Assert.Contains("@action_save_action.OnActivate += (_, args) => save_action(@action_save_action, args);", source);
		Assert.Contains("var @action_save_as_action = global::Gio.SimpleAction.New(\"save-as\", null);", source);
		Assert.Contains("map.AddAction(@action_save_as_action);", source);
		// "save.as" is a different action with its own local and registration.
		Assert.Contains("var @action_save__as_action = global::Gio.SimpleAction.New(\"save.as\", null);", source);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, "var @action_save_as_action = "));
		// Actions are not objects in the .ui, so BuildUi must not look any up.
		Assert.DoesNotContain("builder.GetObject(\"action:", source);
		Assert.DoesNotContain("AddMenuActions", GtkSignalWiring.GenerateCompanion("Main.ui", "Demo", "MainWindow", Handlers()));
	}

	[Fact]
	public void Companion_RegistersApplicationActionsWithParametersAndState()
	{
		var ui = MenuUi.Replace("<item><attribute name=\"label\">Quit</attribute><attribute name=\"action\">app.quit</attribute></item>", """
			<item><attribute name="label">Open</attribute><attribute name="action">app.open</attribute><attribute name="target">'recent'</attribute></item>
			<item><attribute name="label">Dark</attribute><attribute name="action">app.theme</attribute><attribute name="state">'light'</attribute><attribute name="target">'dark'</attribute></item>
			""");
		var source = GtkSignalWiring.GenerateCompanion("Main.ui", "Demo", "MainWindow", GtkSignalWiring.MenuActions(ui));
		Assert.Contains("public void AddApplicationMenuActions(global::Gio.ActionMap map)", source);
		Assert.Contains("Gio.SimpleAction.New(\"open\", global::GLib.Variant.Parse(null, \"'recent'\", null, null).GetType())", source);
		Assert.Contains("Gio.SimpleAction.NewStateful(\"theme\", global::GLib.Variant.Parse(null, \"'dark'\", null, null).GetType()", source);
		Assert.Contains("OnChangeState +=", source);
		Assert.Contains("SetState(args.Value)", source);
	}

	[Fact]
	public void Companion_DerivesAStatefulActionParameterTypeFromItsStateWhenThereIsNoTarget()
	{
		var ui = MenuUi.Replace("<item><attribute name=\"label\">Quit</attribute><attribute name=\"action\">app.quit</attribute></item>", "<item><attribute name=\"label\">Dark</attribute><attribute name=\"action\">app.theme</attribute><attribute name=\"state\">'light'</attribute></item>");
		var source = GtkSignalWiring.GenerateCompanion("Main.ui", "Demo", "MainWindow", GtkSignalWiring.MenuActions(ui));
		Assert.Contains("NewStateful(\"theme\", @action_app_theme_action_state_state.GetType(), @action_app_theme_action_state_state)", source);
		Assert.DoesNotContain("NewStateful(\"theme\", null", source);
	}
}
