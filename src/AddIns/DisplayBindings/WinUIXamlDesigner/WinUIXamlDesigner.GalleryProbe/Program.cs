using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

// Drives the Microsoft WinUI design host over a real app's XAML corpus and reports what opens.
//
// Usage: GalleryProbe <hostDll> <xamlRoot> [appBin] [--keep-class]
//
// Each file gets its own session on its own child process, mirroring the real host RPC sequence
// per DDP session: initialize -> app/resources -> session/open. AppResourceBuilder is the same
// class the IDE side (UnoDesignRuntimeHost.EnsureAppResourcesAsync) uses to turn the app's
// App.xaml into the self-contained dictionary XAML sent over app/resources - skipping that call,
// as an earlier version of this probe did, made ~110 real WinUI-Gallery pages look like designer
// failures when they were actually "StaticResource/local: control resources were never given to
// the child", exactly what app/resources exists to fix.
//
// A page is "opened" only if the host accepted it AND produced a render frame - accepting with
// diagnostics but no pixels is reported separately, because that is what a user would see as an
// empty design surface.

var hostDll = args.ElementAtOrDefault(0) ?? throw new ArgumentException("hostDll required");
var xamlRoot = args.ElementAtOrDefault(1) ?? throw new ArgumentException("xamlRoot required");
var keepClass = args.Contains("--keep-class");
// Passing --appbin makes the client preload the app's own assemblies into the child, which is how
// its local: types resolve (see HostBootstrap.PreloadProjectAssemblies).
var appDir = args.FirstOrDefault(a => a != xamlRoot && Directory.Exists(a));
// Deliberately NOT adopting the app's runtimeconfig: WinUI-Gallery targets net9.0 while the host is
// net10.0, and --runtimeconfig would pin the child to the app's framework and fail to launch it.
var runtimeConfig = "";
var depsFile = "";

var appXamlPath = FindAppXaml(xamlRoot);
var appResourceErrors = new List<string>();
var appResourceXaml = appXamlPath is null ? null : AppResourceBuilder.Build(appXamlPath, appResourceErrors);

var only = args.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?[7..];
var files = Directory.GetFiles(xamlRoot, "*.xaml", SearchOption.AllDirectories)
	.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
		&& !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
	.Where(f => only is null || f.Contains(only, StringComparison.OrdinalIgnoreCase))
	.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
	.ToArray();

Console.WriteLine($"host    : {hostDll}");
Console.WriteLine($"corpus  : {files.Length} .xaml under {xamlRoot}");
Console.WriteLine($"appbin  : {appDir ?? "(none - the app's own types will not resolve)"}");
Console.WriteLine($"x:Class : {(keepClass ? "kept" : "stripped before parsing")}");
Console.WriteLine($"app.xaml: {(appXamlPath is null ? "(not found)"
	: appResourceXaml is null ? $"found but not usable ({string.Join("; ", appResourceErrors)})"
	: $"found, {appResourceXaml.Length} chars of resource XAML built")}");
Console.WriteLine();

var rendered = new List<string>();
var acceptedNoRender = new List<(string File, string Detail)>();
var rejected = new List<(string File, string Detail)>();
var crashed = new List<(string File, string Detail)>();
string? childLog = null;
var appResourcesFailures = 0;

foreach (var file in files)
{
	var name = Path.GetRelativePath(xamlRoot, file);
	var xaml = File.ReadAllText(file);
	if (!keepClass) xaml = StripClassDirective(xaml);

	try
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
		using var client = await UnoDesignClient.StartAsync(runtimeConfig, depsFile, timeout.Token, hostDll, appDir);

		// Matches ConnectAsync's real order: app resources go in once, before any document opens,
		// because they land on Application.Current.Resources rather than on per-document state.
		if (appResourceXaml is not null)
		{
			var resourceResult = await client.SetAppResourcesAsync(appResourceXaml, timeout.Token);
			if (!resourceResult.Success) appResourcesFailures++;
		}

		client.SetViewport(800, 600, 1.0);
		var snapshot = new DesignerDocumentSnapshot {
			SessionId = client.SessionId,
			DocumentId = client.DocumentId,
			Version = 1,
			PrimaryFileName = name,
			Files = { new DesignerSourceFileSnapshot { FileName = name, Kind = "Source", Text = xaml } }
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		var diagnostic = opened.Diagnostics.FirstOrDefault()?.Message ?? "";

		if (!opened.Accepted) rejected.Add((name, diagnostic));
		else if (opened.Render is null || string.IsNullOrEmpty(opened.Render.Data))
			acceptedNoRender.Add((name, diagnostic));
		else rendered.Add(name);

		// Dump the child's own log once, for the first page that fails: it carries the preload
		// report, which is what distinguishes "the app's types never loaded" from a XAML error.
		if (childLog is null && (opened.Render is null || string.IsNullOrEmpty(opened.Render.Data)))
			childLog = $"--- first failure: {name}\n--- diagnostic: {diagnostic}\n--- child log ---\n{client.ChildLog}";
	}
	catch (Exception e)
	{
		crashed.Add((name, e.GetBaseException().Message));
	}
}

Console.WriteLine($"rendered           : {rendered.Count}/{files.Length}");
Console.WriteLine($"opened, no render  : {acceptedNoRender.Count}");
Console.WriteLine($"rejected           : {rejected.Count}");
Console.WriteLine($"host/transport err : {crashed.Count}");
if (appResourceXaml is not null) Console.WriteLine($"app/resources failed: {appResourcesFailures}/{files.Length} sessions");
Console.WriteLine();

Report("REJECTED / NO RENDER (grouped by cause)",
	acceptedNoRender.Concat(rejected).Concat(crashed));

if (rendered.Count > 0) {
	Console.WriteLine();
	Console.WriteLine("RENDERED");
	foreach (var r in rendered) Console.WriteLine("  " + r);
}
if (childLog is not null) {
	Console.WriteLine();
	Console.WriteLine(childLog);
}

void Report(string title, IEnumerable<(string File, string Detail)> items)
{
	var list = items.ToArray();
	if (list.Length == 0) return;
	Console.WriteLine(title);
	foreach (var group in list.GroupBy(i => Summarize(i.Detail)).OrderByDescending(g => g.Count()))
	{
		Console.WriteLine($"  [{group.Count(),3}] {group.Key}");
		foreach (var item in group.Take(3)) Console.WriteLine($"          e.g. {item.File}");
	}
}

// Collapse per-file specifics (type names, positions) so the same underlying cause groups together.
static string Summarize(string detail)
{
	if (string.IsNullOrWhiteSpace(detail)) return "(no diagnostic reported)";
	// WinRT puts a useless "The text associated with this error code could not be found." on the
	// first line and the actual cause underneath, so prefer the first line that says something.
	var line = detail.Split('\n')
		.Select(l => l.Trim())
		.FirstOrDefault(l => l.Length > 0 && !l.StartsWith("The text associated with this error code", StringComparison.Ordinal))
		?? detail.Split('\n')[0].Trim();
	// Drop the per-file source position so the same underlying cause groups together.
	line = System.Text.RegularExpressions.Regex.Replace(line, @"\s*\[Line:\s*\d+\s*Position:\s*\d+\]", "");
	return line.Length > 140 ? line[..140] + "..." : line;
}

// XamlReader.Load rejects x:Class outright - it is a compile-time directive naming a code-behind
// type that does not exist in a runtime parse. Every real page carries one, so a designer that
// cannot see past it can open nothing; strip it the way any runtime XAML loader must.
static string StripClassDirective(string xaml)
	=> System.Text.RegularExpressions.Regex.Replace(
		xaml, @"\s+x:Class\s*=\s*""[^""]*""", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

static string? FindAppXaml(string root)
{
	var candidate = Path.Combine(root, "App.xaml");
	return File.Exists(candidate) ? candidate : null;
}
