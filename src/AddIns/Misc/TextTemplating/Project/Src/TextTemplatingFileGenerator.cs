using System.IO;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.VisualStudio.TextTemplating;

namespace ICSharpCode.TextTemplating
{
	public class TextTemplatingFileGenerator
	{
		public void Generate(FileProjectItem file, IProject project)
		{
			var host = new ProjectFileTemplatingHost(file, project);
			var defaultOutputName = Path.ChangeExtension(file.FileName.ToString(), ".cs");

			var ns = host.GetFileNamespace(defaultOutputName);
			if (ns is not null)
			{
				var sessionHost = (ITextTemplatingSessionHost)host;
				sessionHost.Session ??= sessionHost.CreateSession();
				sessionHost.Session["NamespaceHint"] = ns;
			}

			host.ProcessTemplateAsync(file.FileName.ToString(), defaultOutputName).GetAwaiter().GetResult();

			TextTemplatingService.ShowTemplateHostErrors(host.Errors);
		}
	}
}
