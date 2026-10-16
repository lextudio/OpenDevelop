using System.IO;
using System.Text;
using System.Windows.Markup;
using System.Xml;
using ICSharpCode.WpfDesign.XamlDom;
using Xunit;

namespace OpenDevelop.XamlDom.Tests;

/// <summary>The other half of the comparison: what comes back out. A designer must change only what the user
/// edited, so parsing and saving without an edit has to reproduce the source text exactly. Microsoft's
/// <c>XamlWriter</c> serializes the object graph instead, so it is measured as the contrast.</summary>
public sealed class XamlRoundTripTests
{
	public static IEnumerable<TheoryDataRow<string>> CaseNames() => XamlCorpus.Cases.Select(c => new TheoryDataRow<string>(c.Name));

	static string SaveWithXamlDom(string xaml) => StaRunner.Run(() => SaveWithXamlDomCore(xaml));

	static string SaveWithXamlDomCore(string xaml)
	{
		var document = XamlParser.Parse(new StringReader(xaml));
		var builder = new StringBuilder();
		using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false, NewLineHandling = NewLineHandling.None }))
			document.Save(writer);
		return builder.ToString();
	}

	/// <summary>Cases XamlDom does not reproduce exactly, with why. A case that starts to round-trip must be
	/// removed from here, so the list only ever shrinks. Cases XamlDom cannot parse at all (see
	/// XamlCorpus.NotLoadableByXamlDom) have nothing to save and are skipped.</summary>
	static readonly Dictionary<string, string> KnownRoundTripDifferences = new() {
		["Animations and storyboards"] = "the Storyboard content of BeginStoryboard is rewritten as an explicit <BeginStoryboard.Storyboard> property element",
	};

	[Theory]
	[MemberData(nameof(CaseNames))]
	public void XamlDomSavesWhatItParsedUnchanged(string name)
	{
		if (XamlCorpus.NotLoadableByXamlDom.ContainsKey(name))
			return;
		// The corpus is C# source, so its line endings follow the checkout (CRLF on Windows); XamlDom saves LF.
		var xaml = (XamlCorpus.Prefix + "\n" + XamlCorpus.Cases.Single(c => c.Name == name).Body + "\n</StackPanel>").Replace("\r\n", "\n");
		var saved = Normalize(SaveWithXamlDom(xaml));
		if (KnownRoundTripDifferences.TryGetValue(name, out var reason)) {
			Assert.True(saved != Normalize(xaml), "This case now round-trips; remove it from KnownRoundTripDifferences (was: " + reason + ")");
			return;
		}
		Assert.Equal(Normalize(xaml), saved);
	}

	/// <summary>Contrast, not a requirement: Microsoft's XamlWriter reserializes the live object, dropping the
	/// source's formatting, comments, and anything the object model does not keep. This documents that a
	/// designer cannot be built on it for editing.</summary>
	[Fact]
	public void XamlWriterSerializesEveryCaseButPreservesNone() => StaRunner.Run(() => { XamlWriterSerializesEveryCaseButPreservesNoneCore(); return 0; });

	static void XamlWriterSerializesEveryCaseButPreservesNoneCore()
	{
		var preserved = 0;
		var total = 0;
		var failed = new List<string>();
		foreach (var (name, body) in XamlCorpus.Cases) {
			var xaml = XamlCorpus.Prefix + "\n" + body + "\n</StackPanel>";
			string written;
			try {
				written = XamlWriter.Save(XamlReader.Parse(xaml));
			} catch (Exception e) {
				failed.Add(name + ": " + e.GetType().Name);
				continue;
			}
			total++;
			if (Normalize(written) == Normalize(xaml))
				preserved++;
		}
		// These are the numbers doc/technotes/xaml-loader-comparison.md reports.
		Assert.True(failed.Count == 0, "XamlWriter could not serialize: " + string.Join("; ", failed));
		Assert.Equal(XamlCorpus.Cases.Length, total);
		Assert.Equal(0, preserved);
	}

	/// <summary>Compares markup, not insignificant whitespace between tags or the order the XML writer picks for namespace declarations.</summary>
	static string Normalize(string xml)
	{
		var document = new XmlDocument { PreserveWhitespace = false };
		document.LoadXml(xml);
		return document.OuterXml;
	}
}
