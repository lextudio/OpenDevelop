using System.Windows.Forms;
using System.ComponentModel;

namespace FormsDesigner.CustomControlFixture;

public sealed class FancyButton : Button
{
	[DefaultValue("Blue")]
	public string Accent { get; set; } = "Blue";

	/// <summary>A System.Type-valued property for the designer's type editor.</summary>
	[DefaultValue(null)]
	public System.Type? ItemType { get; set; }
}
