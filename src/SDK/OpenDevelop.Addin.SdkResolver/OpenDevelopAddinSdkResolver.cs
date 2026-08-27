using System;
using System.IO;
using Microsoft.Build.Framework;

namespace OpenDevelop.Addin.SdkResolver;

public sealed class OpenDevelopAddinSdkResolver : SdkResolver
{
    public override string Name => "OpenDevelop Addin SDK Resolver";
    public override int Priority => 5000;

    public override SdkResult Resolve(SdkReference sdkReference, SdkResolverContext context, SdkResultFactory factory)
    {
        if (!string.Equals(sdkReference.Name, "OpenDevelop.Addin.Sdk", StringComparison.OrdinalIgnoreCase))
            return factory.IndicateFailure(null, null);

        // Installed layout: Contents/MacOS/SdkResolvers/<this resolver>/ followed by
        // Contents/MacOS/Sdks/OpenDevelop.Addin.Sdk/Sdk.
        var resolverDirectory = Path.GetDirectoryName(typeof(OpenDevelopAddinSdkResolver).Assembly.Location)!;
        var sdkPath = Path.GetFullPath(Path.Combine(resolverDirectory, "..", "..", "Sdks", "OpenDevelop.Addin.Sdk", "Sdk"));
        if (!Directory.Exists(sdkPath))
            return factory.IndicateFailure($"Installed OpenDevelop Addin SDK was not found at '{sdkPath}'.", null);

        return factory.IndicateSuccess(sdkPath, null, null);
    }
}
