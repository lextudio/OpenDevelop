using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Services;

/// <summary>Where a sampled frame was declared.</summary>
internal sealed record SourceLocation(string FileName, int Line, int Column);

/// <summary>
/// Jumps from a sampled frame to the method's declaration in the open solution. Best effort: a frame names a
/// type and a method but not a file, inlining and generics blur it, and framework or unresolvable frames have
/// no source in the solution at all, so the answer is "not found" rather than a wrong file.
/// </summary>
internal static class GoToSource
{
	static readonly string[] Extensions = [".cs", ".vb"];

	/// <summary>Finds the declaration of the frame's method, or <c>null</c> if the solution has none.</summary>
	public static async Task<SourceLocation?> FindAsync(string frameName, CancellationToken cancellationToken = default)
	{
		if (MethodNameParser.Parse(frameName) is not { } member)
			return null;
		var registry = SD.GetService<LanguageServiceRegistry>();
		if (registry is null)
			return null;

		foreach (var extension in Extensions)
		{
			if (!registry.TryGetService(extension, out var service))
				continue;
			// The parameter count disambiguates overloads; if it matches nothing (a count the frame name could not
			// express), settle for any overload rather than failing.
			var targets = await service.FindMemberAsync(member.TypeName, member.MethodName, member.ParameterCount, cancellationToken);
			if (targets.Count == 0 && member.ParameterCount is not null)
				targets = await service.FindMemberAsync(member.TypeName, member.MethodName, null, cancellationToken);
			if (targets.Count > 0)
				return new SourceLocation(targets[0].FileName, targets[0].Position.Line, targets[0].Position.Column);
		}
		return null;
	}

	/// <summary>Opens the declaration in the editor. Returns whether one was found.</summary>
	public static async Task<bool> GoToAsync(string frameName)
	{
		if (await FindAsync(frameName) is not { } location)
			return false;
		FileService.JumpToFilePosition(location.FileName, location.Line, location.Column);
		return true;
	}
}
