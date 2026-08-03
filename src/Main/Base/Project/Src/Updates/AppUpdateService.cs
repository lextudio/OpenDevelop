// OpenDevelop's update-checking glue, modeled on ILSpy's own AppUpdateService
// (externals/ilspy/ILSpy/Updates/AppUpdateService.cs): a single static carrier for the running
// version and the chosen update strategy. ILSpy sources its version from
// DecompilerVersionInfo (ICSharpCode.Decompiler); OpenDevelop has no Decompiler reference in this
// assembly, so the version comes from the shared RevisionClass instead (linked into this project
// as Properties/GlobalAssemblyInfo.cs - same Major.Minor.Build.Revision shape).
using System;

namespace ICSharpCode.SharpDevelop.Updates
{
	internal enum UpdateStrategy
	{
		NotifyOfUpdates,
		// AutoUpdate
	}

	internal static class AppUpdateService
	{
		public static readonly UpdateStrategy updateStrategy = UpdateStrategy.NotifyOfUpdates;
		public static readonly Version CurrentVersion = new Version(
			RevisionClass.Major + "." + RevisionClass.Minor + "." + RevisionClass.Build + "." + RevisionClass.Revision);
	}
}
