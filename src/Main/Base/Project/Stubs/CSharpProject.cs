using System;
using ICSharpCode.SharpDevelop.Project;

namespace CSharpBinding
{
	public class CSharpProject : CompilableProject
	{
		public CSharpProject(ProjectLoadInformation loadInformation)
			: base(loadInformation)
		{
		}

		public CSharpProject(ProjectCreateInformation info)
			: base(info)
		{
		}

		public override string Language {
			get { return CSharpProjectBinding.LanguageName; }
		}
		
		public override bool UpgradeDesired {
			get {
				if (IsSdkStyleProject)
					return false;
				return base.UpgradeDesired;
			}
		}
	}
}
