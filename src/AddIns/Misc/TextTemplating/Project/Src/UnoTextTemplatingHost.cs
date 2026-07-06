using System;
using System.IO;
using System.Reflection;
using Mono.TextTemplating;

namespace ICSharpCode.TextTemplating
{
	public class UnoTextTemplatingHost : TemplateGenerator
	{
		protected override string ResolveAssemblyReference(string assemblyReference)
		{
			if (Path.IsPathRooted(assemblyReference))
			{
				if (File.Exists(assemblyReference))
					return assemblyReference;
				return null;
			}

			foreach (var referencePath in ReferencePaths)
			{
				var path = Path.Combine(referencePath, assemblyReference);
				if (File.Exists(path))
					return path;
			}

			var name = assemblyReference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
				? assemblyReference.Substring(0, assemblyReference.Length - 4)
				: assemblyReference;

			try
			{
				var asm = Assembly.Load(name);
				if (asm is not null && !asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
					return asm.Location;
			}
			catch { }

			return null;
		}

		protected override bool LoadIncludeText(string requestFileName, out string content, out string location)
		{
			return base.LoadIncludeText(SubstitutePlaceholders(requestFileName), out content, out location);
		}

		protected override string ResolvePath(string path)
		{
			return base.ResolvePath(SubstitutePlaceholders(path));
		}

		protected virtual string SubstitutePlaceholders(string value)
		{
			return ICSharpCode.Core.StringParser.Parse(value);
		}
	}
}
