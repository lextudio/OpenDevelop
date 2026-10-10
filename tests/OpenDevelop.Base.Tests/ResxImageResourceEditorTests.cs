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
}
