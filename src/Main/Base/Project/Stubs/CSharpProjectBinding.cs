using System;
using ICSharpCode.SharpDevelop.Project;

namespace CSharpBinding
{
	public class CSharpProjectBinding : IProjectBinding
	{
		public const string LanguageName = "C#";

		public string Language {
			get { return LanguageName; }
		}

		public IProject LoadProject(ProjectLoadInformation loadInformation)
		{
			return new CSharpProject(loadInformation);
		}

		public IProject CreateProject(ProjectCreateInformation info)
		{
			return new CSharpProject(info);
		}

		public bool HandlingMissingProject {
			get { return false; }
		}
	}
}
