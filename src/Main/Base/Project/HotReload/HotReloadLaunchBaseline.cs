#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ProtocolHotReloadDeltaStatus = ICSharpCode.SharpDevelop.LanguageServices.Protocol.HotReloadDeltaStatus;

namespace ICSharpCode.SharpDevelop.Project.HotReload;

/// <summary>
/// Copies the exact PE and portable PDB selected for launch before the target process starts.
/// This deliberately lives on the IDE side of the protocol: the Roslyn host owns compilations,
/// while only the IDE knows which output file the launch path will execute.
/// </summary>
public static class HotReloadLaunchBaseline
{
	/// <summary>Copies the launch inputs synchronously, before the caller starts the debuggee.</summary>
	public static bool TrySnapshot(HotReloadProjectId project, string assemblyPath,
		out HotReloadBaselineRequest? request, out string diagnostic)
	{
		if (project is null)
			throw new ArgumentNullException(nameof(project));
		request = null;
		if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath)) {
			diagnostic = "The selected launch assembly does not exist: " + assemblyPath;
			return false;
		}
		var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
		if (!File.Exists(pdbPath)) {
			diagnostic = "The selected launch portable PDB does not exist: " + pdbPath;
			return false;
		}
		try {
			request = new HotReloadBaselineRequest(project, File.ReadAllBytes(assemblyPath), File.ReadAllBytes(pdbPath));
			diagnostic = string.Empty;
			return true;
		} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is BadImageFormatException) {
			diagnostic = "Could not snapshot the launch assembly and PDB: " + ex.Message;
			return false;
		}
	}

	public static Task<HotReloadDeltaResponse> SendAsync(IRoslynLanguageProtocol protocol,
		HotReloadBaselineRequest request, CancellationToken cancellationToken)
	{
		if (protocol is null)
			throw new ArgumentNullException(nameof(protocol));
		if (request is null)
			throw new ArgumentNullException(nameof(request));
		return protocol.RoslynHotReloadCaptureBaselineAsync(request, cancellationToken);
	}

	public static async Task<HotReloadDeltaResponse> CaptureAsync(IRoslynLanguageProtocol protocol,
		HotReloadProjectId project, string assemblyPath, CancellationToken cancellationToken)
	{
		if (protocol is null)
			throw new ArgumentNullException(nameof(protocol));
		if (project is null)
			throw new ArgumentNullException(nameof(project));
		if (!TrySnapshot(project, assemblyPath, out var request, out var diagnostic))
			return Failed(diagnostic);
		return await SendAsync(protocol, request!, cancellationToken).ConfigureAwait(false);
	}

	static HotReloadDeltaResponse Failed(string message) =>
		new(ProtocolHotReloadDeltaStatus.Failed, null, message);
}
