using BlueprintBinding;
using Xunit;

namespace BlueprintBinding.Tests;

public sealed class BlueprintCompilerTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "blueprint-" + Guid.NewGuid().ToString("N"));

	public BlueprintCompilerTests() => Directory.CreateDirectory(directory);
	public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

	string Write(string name, string text) { var path = Path.Combine(directory, name); File.WriteAllText(path, text); return path; }

	static string? Compiler() => BlueprintCompilerLocator.Find();

	const string Good = "using Gtk 4.0;\n\ntemplate $MainWindow : Gtk.ApplicationWindow {\n  title: _(\"Demo\");\n  default-width: 400;\n\n  Gtk.Box {\n    orientation: vertical;\n    Gtk.Button run {\n      label: _(\"Run\");\n      clicked => $on_run_clicked();\n    }\n  }\n}\n";
	const string Bad = "using Gtk 4.0;\n\nGtk.Window {\n  title: \"x\"\n  nonsense: 3;\n  Gtk.Foo bar {}\n}\n";

	// The compiler's own output for Bad, colours included (it colours even when piped).
	const string BadOutput = "\u001b[91m\u001b[1merror: Expected `;`\u001b[2m\u001b[0m\nat bad.blp line 5 column 3:\n\u001b[2m   5 |\u001b[0m  nonsense: 3;\n     \u001b[2m|  ^^^^^^^^\u001b[0m\n\n\u001b[91m\u001b[1merror: Class Gtk.Window does not have a property called nonsense\u001b[2m\u001b[0m\nat bad.blp line 5 column 3:\n\u001b[2m   5 |\u001b[0m  nonsense: 3;\n     \u001b[2m|  ^^^^^^^^\u001b[0m\n\u001b[2mhint: Did you check your spelling?\u001b[0m\n\u001b[2mhint: Are your dependencies up to date?\u001b[0m\n\n\u001b[91m\u001b[1merror: Namespace Gtk does not contain a type called Foo\u001b[2m\u001b[0m\nat bad.blp line 6 column 7:\n\u001b[2m   6 |\u001b[0m  Gtk.Foo bar {}\n     \u001b[2m|      ^^^\u001b[0m\n\u001b[2mhint: Did you mean `Box`?\u001b[0m\nsuggestion: replace \u001b[91mFoo\u001b[0m with \u001b[92mBox\u001b[0m\n\n3 errors\n";

	[Fact]
	public void ParseDiagnostics_ReadsCompilerMessagesWithPositionsSpansAndHints()
	{
		var diagnostics = BlueprintCompiler.ParseDiagnostics(BadOutput);
		Assert.Equal(3, diagnostics.Count);
		Assert.Equal(new[] { 5, 5, 6 }, diagnostics.Select(d => d.Line));
		Assert.Equal(new[] { 3, 3, 7 }, diagnostics.Select(d => d.Column));
		Assert.Equal(new[] { 8, 8, 3 }, diagnostics.Select(d => d.Length));
		Assert.All(diagnostics, d => Assert.Equal("error", d.Severity));
		Assert.All(diagnostics, d => Assert.Equal("bad.blp", d.File));
		Assert.Equal("Expected `;`", diagnostics[0].Message);
		Assert.Equal("Class Gtk.Window does not have a property called nonsense", diagnostics[1].Message);
		Assert.Equal(new[] { "Did you check your spelling?", "Are your dependencies up to date?" }, diagnostics[1].Hints);
		Assert.Equal(new[] { "Did you mean `Box`?", "suggestion: replace Foo with Box" }, diagnostics[2].Hints);
		Assert.Empty(BlueprintCompiler.ParseDiagnostics("3 errors\n"));
		Assert.Empty(BlueprintCompiler.ParseDiagnostics(""));
		var warning = Assert.Single(BlueprintCompiler.ParseDiagnostics("warning: Unused thing\nat a.blp line 2 column 1:\n   2 |  x\n     |  ^\n"));
		Assert.Equal("warning", warning.Severity);
		Assert.Equal(1, warning.Length);
	}

	[Fact]
	public void Locator_PrefersTheOverrideThenThePathThenTheFallbackFolders()
	{
		var first = Path.Combine(directory, "first"); var second = Path.Combine(directory, "second"); var fallback = Path.Combine(directory, "fallback");
		foreach (var folder in new[] { first, second, fallback }) Directory.CreateDirectory(folder);
		var name = OperatingSystem.IsWindows() ? "blueprint-compiler.exe" : "blueprint-compiler";
		File.WriteAllText(Path.Combine(second, name), ""); File.WriteAllText(Path.Combine(fallback, name), "");
		var explicitPath = Path.Combine(directory, "custom-compiler"); File.WriteAllText(explicitPath, "");
		var path = first + Path.PathSeparator + second;
		Assert.Equal(explicitPath, BlueprintCompilerLocator.Find(explicitPath, path, new[] { fallback }));
		Assert.Equal(Path.Combine(second, name), BlueprintCompilerLocator.Find(null, path, new[] { fallback }));
		Assert.Equal(Path.Combine(second, name), BlueprintCompilerLocator.Find(Path.Combine(directory, "missing"), path, new[] { fallback })); // an override that does not exist is ignored
		Assert.Equal(Path.Combine(fallback, name), BlueprintCompilerLocator.Find(null, first, new[] { fallback }));
		Assert.Null(BlueprintCompilerLocator.Find(null, first, new[] { Path.Combine(directory, "none") }));
		Assert.Null(BlueprintCompilerLocator.Find(null, null, Array.Empty<string>()));
	}

	[Fact]
	public void Compile_WritesTheUiBesideTheBlueprint()
	{
		Assert.SkipWhen(Compiler() == null, "blueprint-compiler is not installed");
		var blueprint = Write("main.blp", Good);
		var result = BlueprintCompiler.Compile(Compiler()!, blueprint);
		Assert.True(result.Success, result.Failure + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
		Assert.Equal(Path.ChangeExtension(blueprint, ".ui"), result.Output);
		var ui = File.ReadAllText(result.Output!);
		Assert.Contains("<template class=\"MainWindow\" parent=\"GtkApplicationWindow\">", ui);
		Assert.Contains("<signal name=\"clicked\" handler=\"on_run_clicked\"/>", ui);
		Assert.Contains("translatable=\"yes\">Demo</property>", ui);
		// An explicit output path is honoured.
		var elsewhere = Path.Combine(directory, "other.ui");
		Assert.Equal(elsewhere, BlueprintCompiler.Compile(Compiler()!, blueprint, elsewhere).Output);
		Assert.True(File.Exists(elsewhere));
	}

	[Fact]
	public void Compile_ReportsTheCompilersProblemsAndWritesNothing()
	{
		Assert.SkipWhen(Compiler() == null, "blueprint-compiler is not installed");
		var blueprint = Write("bad.blp", Bad);
		var result = BlueprintCompiler.Compile(Compiler()!, blueprint);
		Assert.False(result.Success);
		Assert.Null(result.Failure); // the input had problems; the compiler itself did not fail
		Assert.Equal(new[] { 5, 5, 6 }, result.Diagnostics.Select(d => d.Line));
		Assert.Contains(result.Diagnostics, d => d.Message.StartsWith("Class Gtk.Window does not have a property called nonsense", StringComparison.Ordinal));
		Assert.False(File.Exists(Path.ChangeExtension(blueprint, ".ui")));
	}

	[Fact]
	public void Decompile_ConvertsAUiToBlueprintAndNeverOverwritesByDefault()
	{
		Assert.SkipWhen(Compiler() == null, "blueprint-compiler is not installed");
		var ui = Path.ChangeExtension(Write("main.blp", Good), ".ui");
		Assert.True(BlueprintCompiler.Compile(Compiler()!, Path.ChangeExtension(ui, ".blp")).Success);
		File.Delete(Path.ChangeExtension(ui, ".blp"));
		var result = BlueprintCompiler.Decompile(Compiler()!, ui);
		Assert.True(result.Success, result.Failure);
		var blueprint = File.ReadAllText(result.Output!);
		Assert.StartsWith("using Gtk 4.0;", blueprint);
		Assert.Contains("template $MainWindow: ApplicationWindow {", blueprint);
		Assert.Contains("clicked => $on_run_clicked();", blueprint);
		// A second conversion would overwrite the file the user may have edited: refused unless asked.
		File.WriteAllText(result.Output!, "// edited\n");
		var again = BlueprintCompiler.Decompile(Compiler()!, ui);
		Assert.False(again.Success);
		Assert.Contains("already exists", again.Failure);
		Assert.Equal("// edited\n", File.ReadAllText(result.Output!));
		Assert.True(BlueprintCompiler.Decompile(Compiler()!, ui, overwrite: true).Success);
		Assert.StartsWith("using Gtk 4.0;", File.ReadAllText(result.Output!));
	}

	[Fact]
	public void Decompile_ABrokenUiIsOneFailureLineNotATraceback()
	{
		Assert.SkipWhen(Compiler() == null, "blueprint-compiler is not installed");
		var result = BlueprintCompiler.Decompile(Compiler()!, Write("broken.ui", "<not xml"));
		Assert.False(result.Success);
		Assert.NotNull(result.Failure);
		Assert.DoesNotContain("\n", result.Failure);
		Assert.DoesNotContain("Traceback", result.Failure);
		Assert.Contains("exit 1", result.Failure);
		Assert.False(File.Exists(Path.Combine(directory, "broken.blp")));
	}

	[Fact]
	public void ACompilerThatCannotBeRunIsAFailureNotAnException()
	{
		var result = BlueprintCompiler.Compile(Path.Combine(directory, "no-such-compiler"), Write("main.blp", Good));
		Assert.False(result.Success);
		Assert.StartsWith("Could not run blueprint-compiler", result.Failure);
	}
}
