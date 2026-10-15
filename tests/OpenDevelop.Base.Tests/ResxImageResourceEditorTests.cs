using System.Text;
using ICSharpCode.FormsDesigner.Services;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class ResxImageResourceEditorTests
{
	const string MimeType = "application/x-microsoft.net.object.bytearray.base64";

	[Fact]
	public void ReplaceExistingImage_ReplacesOnlyTheSelectedPayload()
	{
		var original = Encoding.UTF8.GetBytes("""
            <root>
              <!-- <data name="Icon1" type="System.Drawing.Icon, System.Drawing.Common" mimetype="application/x-microsoft.net.object.bytearray.base64"><value>untouched</value></data> -->
              <data name="button.Image" type="System.Drawing.Bitmap, System.Drawing.Common" mimetype="application/x-microsoft.net.object.bytearray.base64" comment="a > b"><value>AQID</value></data>
              <data name="other"><value>keep</value></data>
            </root>
            """);
		var replacement = Png();

		var updated = ResxImageResourceEditor.ReplaceExistingImage(original, "button.Image", replacement);
		var text = Encoding.UTF8.GetString(updated);

		Assert.Contains(Convert.ToBase64String(replacement), text, StringComparison.Ordinal);
		Assert.Contains("<!-- <data name=\"Icon1\"", text, StringComparison.Ordinal);
		Assert.Contains("comment=\"a > b\"", text, StringComparison.Ordinal);
		Assert.Contains("<data name=\"other\"><value>keep</value></data>", text, StringComparison.Ordinal);
		Assert.DoesNotContain("<value>AQID</value>", text, StringComparison.Ordinal);
	}

	[Fact]
	public void ReplaceExistingImage_PreservesUtf16Bom()
	{
		var source = "<root><data name=\"logo\" type=\"System.Drawing.Image, System.Drawing.Common\" mimetype=\"" + MimeType + "\"><value>AQID</value></data></root>";
		var encoding = new UnicodeEncoding(false, true, true);
		var original = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();

		var updated = ResxImageResourceEditor.ReplaceExistingImage(original, "logo", Png());

		Assert.Equal(0xff, updated[0]);
		Assert.Equal(0xfe, updated[1]);
		Assert.Contains(Convert.ToBase64String(Png()), encoding.GetString(updated, 2, updated.Length - 2), StringComparison.Ordinal);
	}

	[Fact]
	public void AddImage_AppendsOnlyANewEscapedEntryAndPreservesUtf16Bom()
	{
		var source = "<root><data name=\"other\"><value>keep</value></data></root>";
		var encoding = new UnicodeEncoding(false, true, true);
		var original = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();

		var updated = ResxImageResourceEditor.AddImage(original, "button.Image & copy", "System.Drawing.Bitmap, System.Drawing.Common", Png());
		var text = encoding.GetString(updated, 2, updated.Length - 2);

		Assert.Equal(0xff, updated[0]);
		Assert.Equal(0xfe, updated[1]);
		Assert.Contains("<data name=\"other\"><value>keep</value></data>", text, StringComparison.Ordinal);
		Assert.Contains("name=\"button.Image &amp; copy\"", text, StringComparison.Ordinal);
		Assert.Contains(Convert.ToBase64String(Png()), text, StringComparison.Ordinal);
		Assert.True(text.IndexOf("button.Image &amp; copy", StringComparison.Ordinal) < text.IndexOf("</root>", StringComparison.Ordinal));
	}

	[Fact]
	public void AddImage_RejectsAnExistingOrUnsupportedEntry()
	{
		var original = Encoding.UTF8.GetBytes("<root>" + ImageResx("image") + "</root>");

		Assert.Throws<InvalidOperationException>(() => ResxImageResourceEditor.AddImage(original, "image", "System.Drawing.Bitmap, System.Drawing.Common", Png()));
		Assert.Throws<NotSupportedException>(() => ResxImageResourceEditor.AddImage(original, "other", "System.Drawing.Metafile, System.Drawing.Common", Png()));
		Assert.Throws<InvalidDataException>(() => ResxImageResourceEditor.AddImage(original, "other", "System.Drawing.Icon, System.Drawing.Common", Png()));
		var selfClosing = Encoding.UTF8.GetBytes("<root><data name=\"image\" /></root>");
		Assert.Throws<InvalidOperationException>(() => ResxImageResourceEditor.AddImage(selfClosing, "image", "System.Drawing.Bitmap, System.Drawing.Common", Png()));
	}

	[Fact]
	public void ReplaceExistingImage_RejectsDuplicateKeys()
	{
		var original = Encoding.UTF8.GetBytes(ImageResx("image") + ImageResx("image"));

		var exception = Assert.Throws<InvalidDataException>(() => ResxImageResourceEditor.ReplaceExistingImage(original, "image", Png()));

		Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ReplaceExistingImage_TreatsNumericAttributeEntitiesAsTheSameKey()
	{
		var original = Encoding.UTF8.GetBytes(ImageResx("button&#46;Image") + ImageResx("button.Image"));

		Assert.Throws<InvalidDataException>(() => ResxImageResourceEditor.ReplaceExistingImage(original, "button.Image", Png()));
	}

	[Fact]
	public void ReplaceExistingImage_ReplacesAnIconOnlyWithAnIcon()
	{
		var original = Encoding.UTF8.GetBytes(ImageResx("icon", "System.Drawing.Icon, System.Drawing.Common"));
		var icon = new byte[] { 0, 0, 1, 0 };

		var updated = ResxImageResourceEditor.ReplaceExistingImage(original, "icon", icon);

		Assert.Contains(Convert.ToBase64String(icon), Encoding.UTF8.GetString(updated), StringComparison.Ordinal);
		Assert.Throws<InvalidDataException>(() => ResxImageResourceEditor.ReplaceExistingImage(original, "icon", Png()));
	}

	[Fact]
	public void ReplaceExistingImage_ReportsAnUnsupportedEncodingClearly()
	{
		var original = Encoding.UTF8.GetBytes("<root>é</root>");
		original[6] = 0xff;

		var exception = Assert.Throws<InvalidDataException>(() => ResxImageResourceEditor.ReplaceExistingImage(original, "image", Png()));

		Assert.Contains("UTF-8 or UTF-16", exception.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("System.Drawing.Bitmap, System.Drawing.Common")]
	[InlineData("System.Drawing.Image, System.Drawing.Common")]
	[InlineData("System.Drawing.Icon, System.Drawing.Common")]
	public void ReplaceExistingImage_RejectsAnInvalidImageHeader(string type)
	{
		var original = Encoding.UTF8.GetBytes(ImageResx("image", type));

		Assert.Throws<InvalidDataException>(() => ResxImageResourceEditor.ReplaceExistingImage(original, "image", new byte[] { 1, 2, 3 }));
	}

	[Fact]
	public void ReplaceExistingImage_RejectsBinaryFormatterPayloads()
	{
		var original = Encoding.UTF8.GetBytes("<root><data name=\"image\" type=\"System.Drawing.Bitmap, System.Drawing.Common\" mimetype=\"application/x-microsoft.net.object.binary.base64\"><value>AQID</value></data></root>");

		Assert.Throws<NotSupportedException>(() => ResxImageResourceEditor.ReplaceExistingImage(original, "image", Png()));
	}

	static string ImageResx(string key, string type = "System.Drawing.Bitmap, System.Drawing.Common") =>
		"<data name=\"" + key + "\" type=\"" + type + "\" mimetype=\"" + MimeType + "\"><value>AQID</value></data>";

	static byte[] Png() => new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

	const string BinaryMime = "application/x-microsoft.net.object.binary.base64";

	[Fact]
	public void SetBinaryObject_AddsAWrappedEntryAndLeavesTheRestAlone()
	{
		var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("<root>\n  <data name=\"other\"><value>keep</value></data>\n</root>")).ToArray();
		var value = new string('A', 200);

		var updated = ResxImageResourceEditor.SetBinaryObject(original, "imageList1.ImageStream", value);
		var text = Encoding.UTF8.GetString(updated, 3, updated.Length - 3).Replace("\r", "");

		Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, updated.Take(3).ToArray());
		Assert.Contains("<data name=\"other\"><value>keep</value></data>", text, StringComparison.Ordinal);
		Assert.Contains("<data name=\"imageList1.ImageStream\" mimetype=\"" + BinaryMime + "\">", text, StringComparison.Ordinal);
		// The value is wrapped at 80 columns between line breaks, as Visual Studio writes it, and still reads back whole.
		Assert.All(text.Split('\n').Where(line => line.Trim().StartsWith("AAAA", StringComparison.Ordinal)), line => Assert.True(line.Trim().Length <= 80));
		Assert.Equal(value, System.Xml.Linq.XDocument.Parse(text).Root!.Elements("data").Single(data => (string?)data.Attribute("name") == "imageList1.ImageStream").Element("value")!.Value.Trim().Replace("\n", "").Replace(" ", ""));
	}

	[Fact]
	public void SetBinaryObject_ReplacesAnExistingStreamInPlaceAndRefusesAnotherKindOfEntry()
	{
		var original = Encoding.UTF8.GetBytes("<root><data name=\"a.ImageStream\" mimetype=\"" + BinaryMime + "\"><value>OLD</value></data><data name=\"x\"><value>keep</value></data></root>");

		var updated = Encoding.UTF8.GetString(ResxImageResourceEditor.SetBinaryObject(original, "a.ImageStream", "NEW"));
		Assert.Contains("<value>NEW</value>", updated, StringComparison.Ordinal);
		Assert.DoesNotContain("OLD", updated, StringComparison.Ordinal);
		Assert.Contains("<data name=\"x\"><value>keep</value></data>", updated, StringComparison.Ordinal);

		var other = Encoding.UTF8.GetBytes("<root><data name=\"a.ImageStream\"><value>text</value></data></root>");
		Assert.Throws<NotSupportedException>(() => ResxImageResourceEditor.SetBinaryObject(other, "a.ImageStream", "NEW"));
		Assert.Throws<ArgumentException>(() => ResxImageResourceEditor.SetBinaryObject(original, "a.ImageStream", " "));
	}

	[Fact]
	public void RemoveBinaryObject_TakesTheWholeEntryAndItsLineAndIsANoOpWhenAbsent()
	{
		var original = Encoding.UTF8.GetBytes("<root>\n  <data name=\"a\"><value>1</value></data>\n  <data name=\"b.ImageStream\" mimetype=\"" + BinaryMime + "\">\n    <value>X</value>\n  </data>\n  <data name=\"c\"><value>3</value></data>\n</root>");

		var removed = Encoding.UTF8.GetString(ResxImageResourceEditor.RemoveBinaryObject(original, "b.ImageStream"));
		Assert.Equal("<root>\n  <data name=\"a\"><value>1</value></data>\n  <data name=\"c\"><value>3</value></data>\n</root>", removed);
		Assert.Same(original, ResxImageResourceEditor.RemoveBinaryObject(original, "missing"));
		var plain = Encoding.UTF8.GetBytes("<root><data name=\"a.ImageStream\"><value>text</value></data></root>");
		Assert.Throws<NotSupportedException>(() => ResxImageResourceEditor.RemoveBinaryObject(plain, "a.ImageStream"));
	}
}
