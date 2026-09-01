using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.FormsDesigner.OutOfProcess;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

/// <summary>Pure DDP contracts that do not require a particular visual runtime or child host.</summary>
public sealed class DesignerRemoteContractsTests
{
	[Fact]
	public void FrameCodec_RoundTripsBgraAndRejectsMismatchedDimensions()
	{
		var original = new byte[] { 1, 2, 3, 4, 10, 20, 30, 40 };
		var frame = new DesignerRenderFrame {
			Width = 2,
			Height = 1,
			Data = DesignerFrameCodec.EncodeDeflateBase64(original)
		};

		Assert.Equal(original, DesignerFrameCodec.DecodeBgra32(frame));
		frame.Width = 1;
		Assert.Throws<InvalidDataException>(() => DesignerFrameCodec.DecodeBgra32(frame));
	}

	[Fact]
	public void HostLaunchSpec_UsesRuntimeGraphOnlyWhenBothInputsExist()
	{
		var directory = Directory.CreateTempSubdirectory("OpenDevelop-DesignerLaunchSpec-");
		try {
			var runtimeConfig = Path.Combine(directory.FullName, "app.runtimeconfig.json");
			var deps = Path.Combine(directory.FullName, "app.deps.json");
			File.WriteAllText(runtimeConfig, "{}");
			File.WriteAllText(deps, "{}");
			var spec = new DesignerHostLaunchSpec {
				RuntimeConfigPath = runtimeConfig,
				DepsFilePath = deps,
				IncludeAppBin = true
			};

			var command = spec.BuildCommandLine("C:\\host.dll", 1234, "token");
			Assert.Contains($"--runtimeconfig \"{runtimeConfig}\" --depsfile \"{deps}\"", command);
			Assert.Contains($"--appbin \"{directory.FullName}\"", command);

			File.Delete(deps);
			command = spec.BuildCommandLine("C:\\host.dll", 1234, "token");
			Assert.DoesNotContain("--runtimeconfig", command);
			Assert.DoesNotContain("--appbin", command);
		} finally {
			directory.Delete(recursive: true);
		}
	}

	[Fact]
	public void DocumentRegistry_OnlyOpenPathCreatesAndEveryLookupValidatesSession()
	{
		var registry = new DesignerDocumentRegistry<object>();
		registry.Initialize("session-a");

		Assert.Throws<InvalidOperationException>(() => registry.Get("session-a", "document-a"));
		Assert.Equal(0, registry.Count);
		Assert.Throws<UnauthorizedAccessException>(() => registry.Get("session-b", "document-a"));

		var opened = registry.GetOrAdd("session-a", "document-a", static () => new object());
		Assert.Same(opened, registry.Get("session-a", "document-a"));
		Assert.Throws<UnauthorizedAccessException>(() => registry.Get("session-b", "document-a"));
	}

	[Fact]
	public void HandshakeValidator_RejectsBadTokensAndUnsupportedProtocols()
	{
		var token = Convert.ToHexString(new byte[] { 1, 2, 3, 4 });
		DesignerHostHandshakeValidator.Validate(token, token, DesignerProtocol.Version);

		Assert.Throws<UnauthorizedAccessException>(() => DesignerHostHandshakeValidator.Validate(token, "00000000", DesignerProtocol.Version));
		Assert.Throws<UnauthorizedAccessException>(() => DesignerHostHandshakeValidator.Validate(token, "not-hex", DesignerProtocol.Version));
		Assert.Throws<UnauthorizedAccessException>(() => DesignerHostHandshakeValidator.Validate("not-hex", token, DesignerProtocol.Version));
		Assert.Throws<NotSupportedException>(() => DesignerHostHandshakeValidator.Validate(token, token, DesignerProtocol.Version + 1));
	}

	[Fact]
	public void EventBindingIsAnExplicitOptionalCapability()
	{
		Assert.True(typeof(IDesignHostEventBinding).IsAssignableFrom(typeof(FormsDesignerHostClient)));
		Assert.True(typeof(IDesignHostBounds).IsAssignableFrom(typeof(FormsDesignerHostClient)));
		Assert.True(typeof(IDesignHostHitTesting).IsAssignableFrom(typeof(FormsDesignerHostClient)));
		Assert.DoesNotContain(typeof(IDesignHostClient).GetMethods(), method => method.Name == nameof(IDesignHostEventBinding.SetEventAsync));
		Assert.DoesNotContain(typeof(IDesignHostClient).GetMethods(), method => method.Name == nameof(IDesignHostBounds.SetBoundsAsync));
		Assert.DoesNotContain(typeof(IDesignHostClient).GetMethods(), method => method.Name == nameof(IDesignHostHitTesting.HitTestAsync));
	}
}
