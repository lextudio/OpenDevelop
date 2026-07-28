using System;

namespace ICSharpCode.CodeCoverage
{
	static class CodeCoverageDotNetHost
	{
		public static string Resolve()
		{
			string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
			return !string.IsNullOrEmpty(host) ? host : "dotnet";
		}
	}
}
