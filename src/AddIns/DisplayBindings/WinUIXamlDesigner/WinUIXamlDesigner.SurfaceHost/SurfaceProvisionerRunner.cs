using System.Diagnostics;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost;

/// <summary>
/// Where the provisioner and its inputs are (the package's <c>HostSDK</c> folder), the project to match, and the
/// short directories it may write to.
/// </summary>
/// <param name="HostSdkDirectory">The unpacked package's HostSDK folder: SurfaceProvisioner/, surface/Surface/, PreviewHost/, engine.stamp.</param>
/// <param name="ProjectFile">The designed project (.csproj); its Windows App SDK version and PRI-bearing packages decide the host.</param>
/// <param name="CacheDirectory">Provisioner cache root. Must be short (see <see cref="SurfaceLaunch.MaxRootLength"/>).</param>
/// <param name="StagingDirectory">Provisioner staging root. Must be short.</param>
/// <param name="DotnetExecutable">The <c>dotnet</c> the provisioner builds with; it otherwise uses the first one on PATH.
/// Pass the SDK the user selected, so the provisioner does not choose another one.</param>
/// <param name="Timeout">Defaults to 15 minutes (a cold build took about 2-4).</param>
public sealed record SurfaceProvisionRequest(
	string HostSdkDirectory, string ProjectFile, string CacheDirectory, string StagingDirectory,
	string? DotnetExecutable = null, TimeSpan? Timeout = null);

/// <summary>Runs the upstream SurfaceProvisioner CLI and parses its one-object-on-stdout result.</summary>
public static class SurfaceProvisionerRunner
{
	public static async Task<SurfaceProvisionResult> RunAsync(
		SurfaceProvisionRequest request, Action<string>? log = null, CancellationToken cancellationToken = default)
	{
		string sdk = request.HostSdkDirectory;
		string exe = Path.Combine(sdk, "SurfaceProvisioner", "SurfaceProvisioner.exe");
		string surfaceProject = Path.Combine(sdk, "surface", "Surface", "Surface.csproj");
		string previewProject = Path.Combine(sdk, "PreviewHost", "PreviewHost.csproj");
		string stampFile = Path.Combine(sdk, "engine.stamp");
		foreach (string required in new[] { exe, surfaceProject, previewProject, stampFile, request.ProjectFile })
			if (!File.Exists(required))
				return SurfaceProvisionResult.Parse("{\"contractVersion\":1,\"status\":\"failed\",\"success\":false,\"error\":"
					+ System.Text.Json.JsonSerializer.Serialize("Missing provisioner input: " + required) + "}");
		SurfaceLaunch.EnsureShortRoot(request.CacheDirectory);
		SurfaceLaunch.EnsureShortRoot(request.StagingDirectory);
		Directory.CreateDirectory(request.CacheDirectory);
		Directory.CreateDirectory(request.StagingDirectory);

		// The package's apphost is built for one architecture (x64). On Windows ARM64 it ran under emulation and, with
		// DOTNET_ROOT pointing at the native ARM64 dotnet below, failed to load that hostfxr (0x800700C1,
		// ERROR_BAD_EXE_FORMAT) and wrote nothing. Run the framework-dependent DLL through the chosen dotnet instead,
		// which is native on every architecture; fall back to the apphost only if the DLL is missing.
		string dll = Path.ChangeExtension(exe, ".dll");
		bool viaDotnet = File.Exists(dll);
		var psi = new ProcessStartInfo(viaDotnet ? request.DotnetExecutable ?? "dotnet" : exe) {
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = Path.GetDirectoryName(request.ProjectFile)!,
		};
		if (viaDotnet) {
			// The DLL targets net10; "Major" uses net10 when the chosen dotnet has it and otherwise the next major
			// (an ARM64 machine may carry only the net11 runtime natively). The apphost resolved the same way only
			// from its own architecture's install.
			psi.ArgumentList.Add("exec");
			psi.ArgumentList.Add("--roll-forward");
			psi.ArgumentList.Add("Major");
			psi.ArgumentList.Add(dll);
		}
		foreach (string argument in new[] {
			"--build", "--project", request.ProjectFile,
			"--surface-project", surfaceProject, "--previewhostpri-project", previewProject,
			"--cache", request.CacheDirectory, "--staging-root", request.StagingDirectory,
			"--engine-stamp", File.ReadAllText(stampFile).Trim(),
		})
			psi.ArgumentList.Add(argument);
		if (request.DotnetExecutable != null) {
			// The provisioner runs a bare "dotnet"; make it the one the user chose.
			string dotnetDirectory = Path.GetDirectoryName(request.DotnetExecutable)!;
			psi.Environment["PATH"] = dotnetDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
			psi.Environment["DOTNET_ROOT"] = dotnetDirectory;
		}

		using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + exe);
		// Both streams must be drained, or a chatty build fills a pipe and stalls the child.
		var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
		var stderrTask = Task.Run(async () => {
			string? line;
			while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) != null)
				log?.Invoke(line);
		}, CancellationToken.None);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(request.Timeout ?? TimeSpan.FromMinutes(15));
		try {
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			try { process.Kill(true); } catch (InvalidOperationException) { }
			if (cancellationToken.IsCancellationRequested)
				throw;
			return SurfaceProvisionResult.Parse("{\"contractVersion\":1,\"status\":\"failed\",\"success\":false,\"error\":\"The provisioner timed out.\"}");
		}
		await stderrTask.ConfigureAwait(false);
		var result = SurfaceProvisionResult.Parse(await stdout.ConfigureAwait(false));
		// Whatever the CLI reported, a non-zero exit is never a success.
		if (process.ExitCode != 0 && result.Success)
			result = result with { Success = false, Status = SurfaceProvisionStatus.Failed, Error = "The provisioner exited with code " + process.ExitCode + "." };
		return result;
	}
}
