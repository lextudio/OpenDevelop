using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Core;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>
	/// Whether an add-in that is about to be installed will load into the running IDE. The add-in tree
	/// enforces the same rules when it loads an add-in (<c>AddInReference.Check</c>), but by then a bad
	/// package has already been installed and the IDE fails on its next start instead of at install time.
	/// </summary>
	public static class AddInCompatibility
	{
		/// <summary>
		/// What stops <paramref name="candidate"/> from working with the add-ins that are present, as
		/// user-presentable sentences; empty when nothing does. The host is the add-in named
		/// <c>SharpDevelop</c>, whose version is the IDE's, so a <c>&lt;Dependency addin="SharpDevelop" version="5.9"/&gt;</c>
		/// is the host range the package was built for.
		/// </summary>
		public static IReadOnlyList<string> Problems(AddIn candidate, IEnumerable<AddIn> present)
		{
			if (candidate == null)
				throw new ArgumentNullException(nameof(candidate));
			var identities = new Dictionary<string, Version>();
			foreach (AddIn addIn in present ?? Enumerable.Empty<AddIn>()) {
				if (addIn == null || addIn == candidate || addIn.Manifest == null)
					continue;
				foreach (KeyValuePair<string, Version> identity in addIn.Manifest.Identities)
					identities[identity.Key] = identity.Value;
			}
			var problems = new List<string>();
			if (candidate.Manifest == null)
				return problems;
			foreach (AddInReference dependency in candidate.Manifest.Dependencies) {
				if (dependency.Check(identities, out Version found))
					continue;
				string required = Range(dependency);
				if (found == null)
					problems.Add("It requires '" + dependency.Name + "'" + required + ", which is not installed.");
				else
					problems.Add("It requires '" + dependency.Name + "'" + required + ", but version " + found + " is installed.");
			}
			foreach (AddInReference conflict in candidate.Manifest.Conflicts) {
				if (conflict.Check(identities, out Version found))
					problems.Add("It cannot be used together with '" + conflict.Name + "' " + found + ", which is installed.");
			}
			return problems;
		}

		static string Range(AddInReference reference)
		{
			Version min = reference.MinimumVersion;
			Version max = reference.MaximumVersion;
			if (min == null || max == null || (min.Major == 0 && min.Minor == 0 && max.Major == int.MaxValue))
				return string.Empty; // no version stated
			return min == max ? " " + min : " " + min + " to " + max;
		}
	}
}
