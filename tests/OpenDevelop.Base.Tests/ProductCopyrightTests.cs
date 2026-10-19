using System;
using System.IO;
using System.Text.RegularExpressions;
using ICSharpCode.SharpDevelop;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class ProductCopyrightTests
{
	static readonly string[] Languages = {
		"StringResources.resx", "StringResources.zh-Hans.resx", "StringResources.zh-Hant.resx"
	};

	[Fact]
	public void LeXtudioYears_IsTheStartYearFollowedByTheCurrentOne()
	{
		// Clock-independent: the year the suite runs in must not be hard-coded in the assertion.
		Assert.Equal(ProductCopyright.Years(ProductCopyright.LeXtudioSince, DateTime.Now.Year), ProductCopyright.LeXtudioYears);
		Assert.StartsWith(ProductCopyright.LeXtudioSince.ToString(), ProductCopyright.LeXtudioYears);
	}

	[Theory]
	[InlineData(2026, 2026, "2026")]
	[InlineData(2026, 2025, "2026")]              // a clock that went backwards still prints one year
	[InlineData(2026, 2027, "2026\u20132027")]    // the en dash the rest of the notice uses
	[InlineData(2026, 2030, "2026\u20132030")]
	public void Years_PrintsASingleYearUntilTheRangeOpens(int from, int to, string expected)
	{
		Assert.Equal(expected, ProductCopyright.Years(from, to));
	}

	[Fact]
	public void Ranges_ComeFromTheConstants()
	{
		Assert.Equal(ProductCopyright.Years(2002, 2016), ProductCopyright.SharpDevelopYears);
		Assert.Equal(ProductCopyright.Years(2011, 2026), ProductCopyright.ILSpyYears);
	}

	[Fact]
	public void Notice_CarriesEveryClauseEvenWithoutTheResourceService()
	{
		// Unit-test hosts have no ResourceService, which is also the app's degraded path: the notice
		// must still name all three clauses rather than throw or blank out.
		Assert.Contains(ProductCopyright.SharpDevelopYears, ProductCopyright.Notice);
		Assert.Contains(ProductCopyright.ILSpyYears, ProductCopyright.Notice);
		Assert.Contains(ProductCopyright.LeXtudioYears, ProductCopyright.Notice);
	}

	[Fact]
	public void EveryLanguageTemplate_HasOnePlaceholderPerClause()
	{
		// The templates are the part of the unit that cannot be exercised without the resource
		// service, so they are read from the repository instead.
		foreach (string language in Languages) {
			string template = CopyrightTemplate(language);
			Assert.Contains("{0}", template);   // SharpDevelop
			Assert.Contains("{1}", template);   // ILSpy
			Assert.Contains("{2}", template);   // LeXtudio
			Assert.DoesNotMatch(@"\{[3-9]|\{[0-9][0-9]", template);
			Assert.Contains("AlphaSierraPapa", template);
			Assert.Contains("ILSpy", template);
			Assert.Contains("LeXtudio", template);
		}
	}

	[Fact]
	public void AssemblyNotice_MatchesTheAttributeTheBuildWrites()
	{
		// Directory.Build.targets must write the attribute at build time and cannot call
		// ProductCopyright, so this is the check that keeps the file and the unit in step.
		Assert.Equal(ProductCopyright.AssemblyNotice,
			ProductCopyright.AssemblyCopyrightOf(typeof(ProductCopyright).Assembly));
	}

	static string CopyrightTemplate(string fileName)
	{
		string path = Path.Combine(RepositoryRoot(), "data", "resources", fileName);
		var match = Regex.Match(File.ReadAllText(path),
			"<data name=\"StartPage\\.Copyright\"[^>]*>\\s*<value>(.*?)</value>", RegexOptions.Singleline);
		Assert.True(match.Success, "StartPage.Copyright not found in " + fileName);
		return match.Groups[1].Value;
	}

	static string RepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (directory != null && !Directory.Exists(Path.Combine(directory, "tests", "fixtures")))
			directory = Path.GetDirectoryName(directory);
		Assert.NotNull(directory);
		return directory;
	}
}
