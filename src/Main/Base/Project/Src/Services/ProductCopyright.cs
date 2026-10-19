using System;
using System.Reflection;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>
	/// The product's copyright notice, defined once. Three surfaces show it - the assembly attribute,
	/// the Start Page footer and the About box - and each used to assemble its own copy, which is how
	/// the year ranges drifted apart (2000 against the license's 2002, 2016 against ILSpy's 2026).
	/// The ranges live here, the wording is one resource template per language with a placeholder per
	/// clause, and every surface renders <see cref="Notice"/>.
	/// </summary>
	public static class ProductCopyright
	{
		/// <summary>AlphaSierraPapa's SharpDevelop years: doc/license.txt and doc/copyright.txt say
		/// 2002-2016, the history's last substantive commit is in 2016 (only 1 commit in 2017 and 2 in
		/// 2019 follow, none substantive), and OpenDevelop's own history starts 2026-07-03.</summary>
		public const int SharpDevelopFrom = 2002, SharpDevelopTo = 2016;

		/// <summary>ILSpy's years: f3a7a412f0 absorbed its WPF files and removed the submodule, and
		/// their per-file notices run from 2011 through 2026.</summary>
		public const int ILSpyFrom = 2011, ILSpyTo = 2026;

		/// <summary>LeXtudio's first year on this product (the history's first OpenDevelop commit is
		/// 2026-07-03). Their range is open, so it ends with the current year.</summary>
		public const int LeXtudioSince = 2026;

		public static string SharpDevelopYears => Years(SharpDevelopFrom, SharpDevelopTo);
		public static string ILSpyYears => Years(ILSpyFrom, ILSpyTo);
		public static string LeXtudioYears => Years(LeXtudioSince, DateTime.Now.Year);

		/// <summary>
		/// The whole notice as a surface shows it: the localized template with each clause's years
		/// filled in. The Start Page footer and the About box both print exactly this string.
		/// </summary>
		public static string Notice {
			get {
				string template = ResourceService.GetString("StartPage.Copyright");
				// A surface must not throw or blank out when the resource service is not up (startup
				// failures already show dialogs); fall back to the English clauses the attribute
				// carries. EveryLanguageTemplate_HasOnePlaceholderPerClause keeps the real templates
				// honest, since this path bypasses them.
				if (string.IsNullOrEmpty(template))
					return "Copyright " + AssemblyNotice + "; © " + LeXtudioYears + " LeXtudio Inc.";
				return string.Format(template, SharpDevelopYears, ILSpyYears, LeXtudioYears);
			}
		}

		/// <summary>
		/// The English clauses the assembly attribute carries. AssemblyCopyright is a build-time
		/// attribute, so Directory.Build.targets writes its own text and cannot call this; the
		/// ProductCopyrightTests compare the two so they cannot drift again.
		/// </summary>
		public static string AssemblyNotice => "© " + SharpDevelopYears + " AlphaSierraPapa for the SharpDevelop Team"
			+ "; © " + ILSpyYears + " AlphaSierraPapa and the ILSpy contributors";

		/// <summary>
		/// The assembly copyright attribute this product is built with, read back so a test can hold
		/// it against <see cref="AssemblyNotice"/>.
		/// </summary>
		public static string AssemblyCopyrightOf(Assembly assembly)
		{
			var attribute = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>();
			return attribute?.Copyright;
		}

		/// <summary>
		/// <paramref name="from"/> alone for a single year, otherwise
		/// "<paramref name="from"/>–<paramref name="to"/>", with the same typographic en dash the rest
		/// of the notice uses.
		/// </summary>
		public static string Years(int from, int to)
		{
			if (from >= to)
				return from.ToString();
			return from + "\u2013" + to;
		}
	}
}
