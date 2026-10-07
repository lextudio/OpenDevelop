using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinUIHotReload;

/// <summary>
/// In-process WinUI 3 Hot Reload agent. It gets WinUI to load WinUIHotReload.Tap into this process
/// by calling InitializeXamlDiagnosticsEx on its OWN pid - the documented XAML Diagnostics entry
/// point, the same one Visual Studio uses, with no debugger involved - and then serves the WPF
/// agent's pipe protocol so the IDE side is shared.
/// </summary>
internal static unsafe class WinUIHotReloadAgent
{
	/// <summary>Must match the uuid on WinUIHotReloadTap in Tap.cpp.</summary>
	static readonly Guid TapClsid = new("3769DAD3-CCF5-4633-81F1-BA08FA59120C");

	/// <summary>The endpoint name WinUI's own XAML Diagnostics launcher uses.</summary>
	const string DiagnosticsEndpoint = "WinUIVisualDiagConnection1";

	static readonly JsonSerializerOptions JsonOptions = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	static int started;
	static volatile TapInterop? tap;
	static volatile string? bootstrapError;
	static readonly XamlDocumentPatcher patcher = new();

	public static void Start()
	{
		var pipeName = Environment.GetEnvironmentVariable("WINUI_HOTRELOAD_PIPE");
		if (string.IsNullOrEmpty(pipeName) || Interlocked.Exchange(ref started, 1) != 0)
			return;

		new Thread(() => {
			Bootstrap();
			// The pipe only opens once bootstrap has finished, either way. The shared session reads
			// any agent.ready answer other than "1" as a failure, so answering while WinUI is still
			// starting would end the session before it began. After a failed bootstrap the pipe
			// opens anyway, so the IDE fails fast with the reason instead of timing out.
			PipeLoop(pipeName);
		}) { IsBackground = true, Name = "WinUI Hot Reload agent" }.Start();
	}

	internal static void Log(string message)
	{
		var path = Environment.GetEnvironmentVariable("WINUI_HOTRELOAD_LOG");
		if (string.IsNullOrEmpty(path))
			return;
		try {
			File.AppendAllText(path, $"[agent tid {Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
		} catch {
			// Logging is best effort; the TAP writes to the same file from native threads.
		}
	}

	static void Bootstrap()
	{
		try {
			var tapPath = LocateTap();
			if (tapPath == null) {
				bootstrapError = $"no TAP for {RuntimeInformation.ProcessArchitecture} next to the agent";
				Log(bootstrapError);
				return;
			}

			// FrameworkUdk exports InitializeXamlDiagnosticsEx; Microsoft.UI.Xaml has to be loaded and
			// its core up before the request can be served. Both load once the application starts
			// WinUI, which a startup hook runs well before.
			var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
			nint udk = 0, xaml = 0;
			while (DateTime.UtcNow < deadline
				&& ((udk = GetModuleHandleW("Microsoft.Internal.FrameworkUdk.dll")) == 0
					|| (xaml = GetModuleHandleW("Microsoft.ui.xaml.dll")) == 0))
				Thread.Sleep(100);
			if (udk == 0 || xaml == 0) {
				bootstrapError = "WinUI never loaded in this process; is it a WinUI 3 application?";
				Log(bootstrapError);
				return;
			}

			// Loaded up front so its exports are callable, and so WinUI's load of the same path
			// resolves to this module rather than a second copy.
			var module = NativeLibrary.Load(tapPath);
			var interop = new TapInterop(module);
			var initialize = (delegate* unmanaged[Stdcall]<char*, uint, char*, char*, Guid, char*, int>)
				NativeLibrary.GetExport(udk, "InitializeXamlDiagnosticsEx");
			var xamlPath = ModulePath(xaml);
			Log($"TAP '{tapPath}', Microsoft.UI.Xaml '{xamlPath}'");

			// Before the XAML core has a dispatcher the request either fails or is accepted and
			// dropped, so keep asking until the TAP actually reports a site.
			int hr = 0;
			while (DateTime.UtcNow < deadline) {
				fixed (char* endpoint = DiagnosticsEndpoint)
				fixed (char* xamlDll = xamlPath)
				fixed (char* tapDll = tapPath)
					hr = initialize(endpoint, (uint)Environment.ProcessId, xamlDll, tapDll, TapClsid, null);
				Log($"InitializeXamlDiagnosticsEx hr=0x{hr:X8}");
				if (hr >= 0 && WaitUntil(() => interop.IsReady, TimeSpan.FromSeconds(5))) {
					tap = interop;
					Log("TAP ready");
					return;
				}
				Thread.Sleep(500);
			}
			bootstrapError = $"WinUI did not load the TAP (last InitializeXamlDiagnosticsEx hr=0x{hr:X8})";
			Log(bootstrapError);
		} catch (Exception ex) {
			bootstrapError = ex.Message;
			Log("Bootstrap failed: " + ex);
		}
	}

	/// <summary>tap/&lt;arch&gt;/ beside the agent: the TAP must match this process, not the IDE.</summary>
	static string? LocateTap()
	{
		var arch = RuntimeInformation.ProcessArchitecture switch {
			Architecture.X86 => "x86",
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => null,
		};
		if (arch == null)
			return null;
		var agentDirectory = Path.GetDirectoryName(typeof(WinUIHotReloadAgent).Assembly.Location)!;
		var path = Path.Combine(agentDirectory, "tap", arch, "WinUIHotReload.Tap.dll");
		return File.Exists(path) ? path : null;
	}

	static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
	{
		var watch = Stopwatch.StartNew();
		while (watch.Elapsed < timeout) {
			if (condition())
				return true;
			Thread.Sleep(100);
		}
		return condition();
	}

	static void PipeLoop(string pipeName)
	{
		Log($"pipe '{pipeName}' listening");
		while (true) {
			try {
				// One request per connection, like the WPF agent, so the IDE's pipe client is shared.
				using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
				server.WaitForConnection();
				using var reader = new StreamReader(server, new UTF8Encoding(false));
				using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
				var line = reader.ReadLine();
				if (line == null)
					continue;
				writer.WriteLine(JsonSerializer.Serialize(Handle(line), JsonOptions));
			} catch (Exception ex) {
				Log("pipe error: " + ex.Message);
				Thread.Sleep(100);
			}
		}
	}

	static PipeResponse Handle(string line)
	{
		PipeRequest? request;
		try {
			request = JsonSerializer.Deserialize<PipeRequest>(line, JsonOptions);
		} catch (JsonException ex) {
			return new PipeResponse { Result = "error: invalid request: " + ex.Message };
		}
		if (request == null)
			return new PipeResponse { Result = "error: invalid request" };

		if (request.Kind == "query")
			return Query(request.Query ?? "");

		if (request.Kind == "apply") {
			var current = tap;
			if (current == null)
				return new PipeResponse { Result = "error: the WinUI XAML Diagnostics TAP is not loaded" + (bootstrapError is null ? " yet" : ": " + bootstrapError) };
			if (request.FilePath == null || request.XamlText == null)
				return new PipeResponse { Result = "error: apply needs filePath and xamlText" };
			try {
				var result = patcher.Apply(current, request.FilePath, request.PreviousXamlText, request.XamlText);
				Log($"apply {request.FilePath}: {result}");
				return new PipeResponse { Result = result };
			} catch (Exception ex) {
				Log("apply failed: " + ex);
				return new PipeResponse { Result = $"error: {ex.GetType().Name}: {ex.Message}" };
			}
		}

		return new PipeResponse { Result = $"error: unknown request kind '{request.Kind}'" };
	}

	/// <summary>
	/// "agent.ready", or "&lt;x:Name&gt;.&lt;Property&gt;" for a string property of a named element - the
	/// latter lets a test check what the running application shows without trusting the apply path.
	/// </summary>
	static PipeResponse Query(string query)
	{
		if (query == "agent.ready")
			return tap != null
				? new PipeResponse { Result = "ok", Value = "1" }
				: new PipeResponse { Result = "error: " + bootstrapError, Value = "0" };

		var current = tap;
		if (current == null)
			return new PipeResponse { Result = "error: not ready" };

		var dot = query.LastIndexOf('.');
		if (dot <= 0)
			return new PipeResponse { Result = $"error: unknown query '{query}'" };
		var name = query.Substring(0, dot);
		var property = query.Substring(dot + 1);
		var element = current.GetElements().FirstOrDefault(e => e.Name == name);
		if (element == null)
			return new PipeResponse { Result = $"error: no live element named '{name}'" };
		var resolved = WinUIPropertyResolver.Resolve(element.Type, property);
		var value = current.GetStringProperty(element.Handle, resolved.FullName, out var hr);
		return hr < 0
			? new PipeResponse { Result = $"error: reading {resolved.FullName} failed (hr=0x{hr:X8})" }
			: new PipeResponse { Result = "ok", Value = value };
	}

	static string ModulePath(nint module)
	{
		var buffer = new char[1024];
		fixed (char* p = buffer) {
			var length = GetModuleFileNameW(module, p, (uint)buffer.Length);
			return new string(p, 0, (int)length);
		}
	}

	[DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
	static extern nint GetModuleHandleW(string name);

	[DllImport("kernel32", ExactSpelling = true)]
	static extern uint GetModuleFileNameW(nint module, char* fileName, uint size);

	sealed class PipeRequest
	{
		public string? Kind { get; set; }
		public string? Query { get; set; }
		public string? FilePath { get; set; }
		public string? XamlText { get; set; }
		public string? PreviousXamlText { get; set; }
		public string? ChangeKind { get; set; }
	}

	sealed class PipeResponse
	{
		public string? Result { get; set; }
		public string? Value { get; set; }
	}
}
