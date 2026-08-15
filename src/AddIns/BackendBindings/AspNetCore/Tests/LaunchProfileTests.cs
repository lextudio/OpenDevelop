using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ICSharpCode.AspNetCore;
using Xunit;

namespace AspNetCore.Tests;

public sealed class LaunchProfileTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "opendevelop-aspnetcore-" + Guid.NewGuid().ToString("N"));

	public LaunchProfileTests() => Directory.CreateDirectory(directory);
	public void Dispose() => Directory.Delete(directory, recursive: true);

	[Fact]
	public void LoadsProjectProfileAndBuildsDotnetRunCommand()
	{
		Directory.CreateDirectory(Path.Combine(directory, "Properties"));
		File.WriteAllText(Path.Combine(directory, "Properties", "launchSettings.json"), """
		{
		  "profiles": {
		    "Web": {
		      "commandName": "Project",
		      "commandLineArgs": "--seed \"two words\"",
		      "launchBrowser": true,
		      "launchUrl": "swagger",
		      "applicationUrl": "https://localhost:7001;http://localhost:7000",
		      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "CUSTOM": "yes" }
		    }
		  }
		}
		""");
		var provider = new AspNetCoreLaunchProfileProvider(directory, "Web");
		provider.LoadLaunchSettings();
		var profile = Assert.IsType<AspNetCoreLaunchProfile>(provider.GetProfile());

		Assert.Equal("https://localhost:7001/swagger", profile.GetBrowserUrl());
		var command = AspNetCoreLaunchCommand.Create(Path.Combine(directory, "Web.csproj"), profile);
		Assert.Equal("dotnet", command.FileName);
		Assert.Equal(directory, command.WorkingDirectory);
		Assert.Equal(new[] { "run", "--no-build", "--project", Path.Combine(directory, "Web.csproj"), "--", "--seed", "two words" }, command.ArgumentList);
		Assert.Equal("https://localhost:7001;http://localhost:7000", command.Environment["ASPNETCORE_URLS"]);
		Assert.Equal("yes", command.Environment["CUSTOM"]);
	}

	[Fact]
	public void SavePreservesGlobalSettingsAndUnknownProfileSettings()
	{
		Directory.CreateDirectory(Path.Combine(directory, "Properties"));
		var path = Path.Combine(directory, "Properties", "launchSettings.json");
		File.WriteAllText(path, """
		{ "iisSettings": { "windowsAuthentication": true }, "profiles": {
		  "Web": {
		    "commandName": "Project",
		    "applicationUrl": "http://localhost:8123",
		    "inspectUri": "custom",
		    "customObject": { "enabled": true, "ports": [5000, 5001], "optional": null }
		  }
		} }
		""");
		var provider = new AspNetCoreLaunchProfileProvider(directory, "Web");
		provider.LoadLaunchSettings();
		provider.UpdateProfile("Web", "http://localhost:9000", "health", launchBrowser: true);
		provider.SaveLaunchSettings();

		using var json = JsonDocument.Parse(File.ReadAllText(path));
		Assert.True(json.RootElement.GetProperty("iisSettings").GetProperty("windowsAuthentication").GetBoolean());
		Assert.Equal("custom", json.RootElement.GetProperty("profiles").GetProperty("Web").GetProperty("inspectUri").GetString());
		var custom = json.RootElement.GetProperty("profiles").GetProperty("Web").GetProperty("customObject");
		Assert.True(custom.GetProperty("enabled").GetBoolean());
		Assert.Equal(new[] { 5000, 5001 }, custom.GetProperty("ports").EnumerateArray().Select(p => p.GetInt32()));
		Assert.Equal(JsonValueKind.Null, custom.GetProperty("optional").ValueKind);
		Assert.Equal("http://localhost:9000", json.RootElement.GetProperty("profiles").GetProperty("Web").GetProperty("applicationUrl").GetString());
		Assert.Equal("health", json.RootElement.GetProperty("profiles").GetProperty("Web").GetProperty("launchUrl").GetString());
	}

	[Fact]
	public void MissingFileGetsInMemoryDefaultWithoutWritingUnlessRequested()
	{
		var provider = new AspNetCoreLaunchProfileProvider(directory, "Web");
		provider.LoadLaunchSettings();
		Assert.False(File.Exists(provider.LaunchSettingsJsonPath));
		Assert.Equal("http://localhost:5000", provider.GetProfile()!.ApplicationUrl);

		provider.SaveLaunchSettings();
		Assert.True(File.Exists(provider.LaunchSettingsJsonPath));
	}

	[Fact]
	public void LoadsPublishProfileAndBuildsArgumentSafeCommand()
	{
		var profilesDirectory = Path.Combine(directory, "Properties", "PublishProfiles");
		Directory.CreateDirectory(profilesDirectory);
		File.WriteAllText(Path.Combine(profilesDirectory, "Folder.pubxml"), """
		<Project><PropertyGroup>
		  <WebPublishMethod>FileSystem</WebPublishMethod>
		  <LastUsedBuildConfiguration>Release</LastUsedBuildConfiguration>
		  <TargetFramework>net10.0</TargetFramework>
		  <RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
		  <SelfContained>true</SelfContained>
		  <PublishUrl>artifacts/publish output</PublishUrl>
		</PropertyGroup></Project>
		""");

		var profile = Assert.Single(AspNetCorePublishCommand.LoadProfiles(directory));
		var command = AspNetCorePublishCommand.Create(Path.Combine(directory, "Web.csproj"), profile);
		Assert.Equal("Folder", profile.Name);
		Assert.Equal(new[] { "publish", Path.Combine(directory, "Web.csproj"), "--configuration", "Release", "--framework", "net10.0", "--runtime", "osx-arm64", "--self-contained", "true", "--output", Path.Combine(directory, "artifacts", "publish output") }, command.ArgumentList);

		var changed = new AspNetCorePublishProfile { FilePath = profile.FilePath, Name = profile.Name, PublishDirectory = "new output", Configuration = "Debug", TargetFramework = profile.TargetFramework, RuntimeIdentifier = profile.RuntimeIdentifier, SelfContained = false };
		changed.Save();
		var reloaded = AspNetCorePublishProfile.Load(profile.FilePath);
		Assert.Equal("new output", reloaded.PublishDirectory);
		Assert.Equal("Debug", reloaded.Configuration);
		Assert.False(reloaded.SelfContained);
		Assert.Contains("<WebPublishMethod>FileSystem</WebPublishMethod>", File.ReadAllText(profile.FilePath));
	}

	[Fact]
	[Trait("Category", "Integration")]
	public async Task PublishesARealWebProjectToFolder()
	{
		File.WriteAllText(Path.Combine(directory, "PublishWeb.csproj"), """
		<Project Sdk="Microsoft.NET.Sdk.Web">
		  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
		</Project>
		""");
		File.WriteAllText(Path.Combine(directory, "Program.cs"), "var app = WebApplication.Create(args); app.Run();");
		var output = Path.Combine(directory, "published output");
		var command = AspNetCorePublishCommand.Create(Path.Combine(directory, "PublishWeb.csproj"),
			new AspNetCorePublishProfile { Name = "Folder", PublishDirectory = output, Configuration = "Release" });
		command.RedirectStandardOutput = true;
		command.RedirectStandardError = true;
		using var process = Process.Start(command)!;
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		var standardOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		Assert.True(process.ExitCode == 0, standardOutput + Environment.NewLine + standardError);
		Assert.True(File.Exists(Path.Combine(output, "PublishWeb.dll")));
		Assert.True(File.Exists(Path.Combine(output, "PublishWeb.runtimeconfig.json")));
	}

	[Fact]
	public void ParsesMachineReadableDevelopmentCertificateStatus()
	{
		var trusted = AspNetCoreDevCertificate.AnalyzeMachineReadableCheck(0,
			"[{\"Thumbprint\":\"ABC\",\"TrustLevel\":\"Full\"}]", string.Empty);
		var untrusted = AspNetCoreDevCertificate.AnalyzeMachineReadableCheck(0,
			"[{\"Thumbprint\":\"ABC\",\"TrustLevel\":\"None\"}]", string.Empty);
		var missing = AspNetCoreDevCertificate.AnalyzeMachineReadableCheck(0, "[]", string.Empty);
		var error = AspNetCoreDevCertificate.AnalyzeMachineReadableCheck(1, string.Empty, "SDK unavailable");

		Assert.Equal(AspNetCoreDevCertificateStatus.Trusted, trusted.Status);
		Assert.Equal(AspNetCoreDevCertificateStatus.Untrusted, untrusted.Status);
		Assert.Equal(AspNetCoreDevCertificateStatus.Missing, missing.Status);
		Assert.Equal(AspNetCoreDevCertificateStatus.Error, error.Status);
		Assert.Contains("SDK unavailable", error.Message);
	}

	[Fact]
	public void CreatesModernInteractiveScaffoldingCommand()
	{
		var command = AspNetCoreScaffolding.CreateInteractiveTerminalCommand(directory);
		Assert.DoesNotContain("aspnet-codegenerator", command.ArgumentList);
		Assert.Contains(command.ArgumentList, argument => argument.Contains("scaffold", StringComparison.Ordinal));
		Assert.Contains("Microsoft.dotnet-scaffold", AspNetCoreScaffolding.InstallCommand);
	}

	[Fact]
	public void ResolvesAndLaunchesTheRestoredBlazorWebAssemblyDevServer()
	{
		var packageRoot = Path.Combine(directory, "packages");
		var packagePath = Path.Combine(packageRoot, "microsoft.aspnetcore.components.webassembly.devserver", "10.0.10");
		var serverPath = Path.Combine(packagePath, "tools", "blazor-devserver.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
		File.WriteAllText(serverPath, string.Empty);
		Directory.CreateDirectory(Path.Combine(directory, "obj"));
		File.WriteAllText(Path.Combine(directory, "obj", "project.assets.json"), $$"""
		{
		  "libraries": {
		    "Microsoft.AspNetCore.Components.WebAssembly.DevServer/10.0.10": {
		      "type": "package",
		      "path": "microsoft.aspnetcore.components.webassembly.devserver/10.0.10"
		    }
		  },
		  "packageFolders": { "{{packageRoot.Replace("\\", "\\\\")}}/": {} }
		}
		""");
		var applicationPath = Path.Combine(directory, "bin", "Debug", "net10.0", "Client.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(applicationPath)!);
		File.WriteAllText(applicationPath, string.Empty);
		Directory.CreateDirectory(Path.Combine(directory, "Properties"));
		File.WriteAllText(Path.Combine(directory, "Properties", "launchSettings.json"), """
		{
		  "profiles": {
		    "Client": {
		      "commandName": "Project",
		      "applicationUrl": "http://localhost:5149",
		      "inspectUri": "{wsProtocol}://{url.hostname}:{url.port}/_framework/debug/ws-proxy?browser={browserInspectUri}"
		    }
		  }
		}
		""");

		var provider = new AspNetCoreLaunchProfileProvider(directory, "Client");
		provider.LoadLaunchSettings();
		var profile = provider.GetProfile()!;
		Assert.Contains("/_framework/debug/ws-proxy", profile.InspectUri);
		Assert.True(BlazorWebAssemblyDevServer.TryCreate(Path.Combine(directory, "Client.csproj"), applicationPath, profile, out var command));
		Assert.Equal("dotnet", command.FileName);
		Assert.Equal(new[] { serverPath, "--applicationpath", applicationPath }, command.ArgumentList);
		Assert.Equal("http://localhost:5149", command.Environment["ASPNETCORE_URLS"]);
	}

	[Fact]
	[Trait("Category", "Integration")]
	public async Task ChecksDevelopmentCertificateWithoutChangingIt()
	{
		var result = await AspNetCoreDevCertificate.CheckAsync(TestContext.Current.CancellationToken);
		Assert.NotEqual(AspNetCoreDevCertificateStatus.Error, result.Status);
		Assert.False(string.IsNullOrWhiteSpace(result.Message));
	}

	[Fact]
	[Trait("Category", "Integration")]
	public async Task GeneratedCommandStartsARealKestrelApplication()
	{
		var port = GetFreeTcpPort();
		File.WriteAllText(Path.Combine(directory, "Web.csproj"), """
		<Project Sdk="Microsoft.NET.Sdk.Web">
		  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
		</Project>
		""");
		File.WriteAllText(Path.Combine(directory, "Program.cs"), """
		var builder = WebApplication.CreateBuilder(args);
		var app = builder.Build();
		app.MapGet("/ready", () => "ready");
		app.Run();
		""");
		var provider = new AspNetCoreLaunchProfileProvider(directory, "Web");
		provider.LoadLaunchSettings();
		provider.AddProjectProfile("Web", $"http://127.0.0.1:{port}");
		var command = AspNetCoreLaunchCommand.Create(Path.Combine(directory, "Web.csproj"), provider.GetProfile()!, noBuild: false);
		command.RedirectStandardOutput = true;
		command.RedirectStandardError = true;
		using var process = Process.Start(command)!;
		var cancellationToken = TestContext.Current.CancellationToken;
		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
			while (DateTime.UtcNow < deadline && !process.HasExited)
			{
				try
				{
					Assert.Equal("ready", await client.GetStringAsync($"http://127.0.0.1:{port}/ready", cancellationToken));
					return;
				}
				catch (HttpRequestException) { }
				catch (TaskCanceledException) { }
				await Task.Delay(100, cancellationToken);
			}
			var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
			var error = await process.StandardError.ReadToEndAsync(cancellationToken);
			Assert.Fail($"Kestrel did not become ready. Exit={process.HasExited} Code={(process.HasExited ? process.ExitCode : -1)}\n{output}\n{error}");
		}
		finally
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
			process.WaitForExit(5000);
		}
	}

	static int GetFreeTcpPort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}
}
