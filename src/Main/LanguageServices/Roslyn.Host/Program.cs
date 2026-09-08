// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using StreamJsonRpc;

namespace ICSharpCode.SharpDevelop.LanguageServices.Host
{
	/// <summary>
	/// The Roslyn language host process.
	///
	/// It is intentionally thin. Transport, handshake, ready signalling, parent-disconnect handling
	/// and exit-code policy all come from <see cref="DesignerChildHost"/>, which the designers
	/// already use and which is already tested; the language behaviour comes from the same
	/// <see cref="InProcessRoslynLanguageProtocol"/> adapter the IDE runs today. What is left here
	/// is only the wiring between them, which is the result of Phases 1-2 having made the surface
	/// serialisable and the consumers protocol-shaped before any process existed.
	///
	/// Note what this host does NOT do: it does not discover projects. MSBuild evaluation stays in
	/// the IDE, which pushes the project graph over <c>roslyn/project/load</c>
	/// (doc/technotes/roslyn-host-process.md §4, §7a). A host that evaluated projects itself would
	/// duplicate the IDE's project model and the two would drift.
	/// </summary>
	static class Program
	{
		static readonly ManualResetEventSlim shutdown = new(false);

		static int Main(string[] args)
		{
			System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
			System.Diagnostics.Trace.AutoFlush = true;
			return DesignerChildHost.Run(
				args,
				readyMessagePrefix: "ROSLYN-HOST-READY",
				registerMethods: RegisterMethods,
				waitForShutdown: () => shutdown.Wait(),
				onParentDisconnected: () => shutdown.Set());
		}

		/// <summary>
		/// Registers one RPC method per protocol method, taking the names from
		/// <see cref="RoslynProtocolMethods"/> so the host cannot drift from the client.
		/// </summary>
		static void RegisterMethods(JsonRpc rpc, string token)
		{
			// The host owns its own language service and workspace - that ownership moving here is
			// the entire point of the exercise.
			//
			// Construct lazily so a cold host can answer status without creating a Roslyn workspace.
			// CSharpVBLanguageService treats IDE services as optional, so a bare host can construct
			// it and receive the parent-owned project graph over roslyn/project/load. The remaining
			// csproj split is still needed to make that independence mechanical and UI-free.
			var languageService = new Lazy<Roslyn.CSharpVBLanguageService>(CreateLanguageService);
			var protocol = new Lazy<IRoslynLanguageProtocol>(() =>
				new InProcessRoslynLanguageProtocol(
					languageService.Value,
					projectLoad: languageService.Value.LoadProjectAsync,
					solutionClosed: languageService.Value.CloseSolutionAsync,
					workspaceStatusProvider: languageService.Value.GetWorkspaceStatus));
			var dispatcher = new Lazy<RoslynProtocolDispatcher>(() =>
				new RoslynProtocolDispatcher(protocol.Value));

			// The handshake the parent performs before any protocol traffic
			// (DesignerHostProcessClient.OnConnectedAsync). Echoing the session id back is what lets
			// the parent detect a stale or reused child rather than talking to the wrong process.
			rpc.AddLocalRpcMethod("initialize", new Func<string, int, string, HostHandshake>(
				(token, protocolVersion, sessionId) => new HostHandshake {
					ProtocolVersion = protocolVersion,
					Runtime = "roslyn",
					ProcessId = Environment.ProcessId,
					SessionId = sessionId,
				}));

			foreach (var method in RoslynProtocolDispatcher.SupportedMethods)
			{
				var captured = method;
				// Answerable without a language service, and the first thing a client asks while the
				// host warms up - so it must not be gated on the lazy construction above.
				if (captured == RoslynProtocolMethods.Status)
				{
					rpc.AddLocalRpcMethod(captured, new Func<Task<WorkspaceStatus>>(
						() => protocol.IsValueCreated
							? protocol.Value.RoslynStatusAsync(CancellationToken.None)
							: Task.FromResult(new WorkspaceStatus(Array.Empty<ProjectStatus>()))));
					continue;
				}
				rpc.AddLocalRpcMethod(captured, new Func<JsonRpcArguments, CancellationToken, Task<object?>>(
					(arguments, cancellationToken) => dispatcher.Value.DispatchAsync(captured, arguments, cancellationToken)));
			}
		}

		/// <summary>
		/// The language service this host serves.
		///
		/// Kept as its own method because it is the seam where the host's Roslyn workspace is
		/// created and, later, where a persisted workspace would be restored from <c>.od/</c>
		/// (§7b) - the thing that makes a cold start fast rather than merely out-of-process.
		/// </summary>
		static Roslyn.CSharpVBLanguageService CreateLanguageService() =>
			new Roslyn.CSharpVBLanguageService();

		/// <summary>
		/// Argument reader over the JSON payload StreamJsonRpc hands us.
		///
		/// The dispatcher deliberately does not know about JSON, so the serialiser-specific part
		/// lives here, on the host side, where the serialiser is chosen.
		/// </summary>
		sealed class JsonRpcArguments : IRoslynProtocolArguments
		{
			public TextDocumentIdentifier? document { get; set; }
			public int offset { get; set; }
			public string? text { get; set; }
			public string? newName { get; set; }
			public string? actionId { get; set; }
			public TextSpan? span { get; set; }
			public LanguageServiceProjectSnapshot? snapshot { get; set; }
			public string? typeFullName { get; set; }
			public string? methodName { get; set; }
			public int? parameterCount { get; set; }
			public string? interfaceName { get; set; }
			public IReadOnlyList<string>? memberIds { get; set; }
			public bool addInterfaceToClass { get; set; }
			public bool includeComments { get; set; }
			public bool includeDiagnostics { get; set; }
			public bool renameOverloads { get; set; }
			public bool renameInStrings { get; set; }
			public bool renameInComments { get; set; }

			TextDocumentIdentifier IRoslynProtocolArguments.Document() =>
				document ?? throw new InvalidOperationException("Request is missing 'document'.");
			int IRoslynProtocolArguments.Offset() => offset;
			string IRoslynProtocolArguments.Text() => text ?? string.Empty;
			string IRoslynProtocolArguments.NewName() => newName ?? string.Empty;
			string IRoslynProtocolArguments.ActionId() => actionId ?? string.Empty;
			TextSpan? IRoslynProtocolArguments.Span() => span;
			LanguageServiceProjectSnapshot IRoslynProtocolArguments.Snapshot() =>
				snapshot ?? throw new InvalidOperationException("Request is missing 'snapshot'.");
			string IRoslynProtocolArguments.TypeFullName() => typeFullName ?? string.Empty;
			string IRoslynProtocolArguments.MethodName() => methodName ?? string.Empty;
			int? IRoslynProtocolArguments.ParameterCount() => parameterCount;
			string IRoslynProtocolArguments.InterfaceName() => interfaceName ?? string.Empty;
			IReadOnlyList<string> IRoslynProtocolArguments.MemberIds() => memberIds ?? Array.Empty<string>();
			bool IRoslynProtocolArguments.AddInterfaceToClass() => addInterfaceToClass;
			bool IRoslynProtocolArguments.IncludeComments() => includeComments;
			bool IRoslynProtocolArguments.IncludeDiagnostics() => includeDiagnostics;
			bool IRoslynProtocolArguments.RenameOverloads() => renameOverloads;
			bool IRoslynProtocolArguments.RenameInStrings() => renameInStrings;
			bool IRoslynProtocolArguments.RenameInComments() => renameInComments;
		}
	}
}
