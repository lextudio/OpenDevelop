using System.Windows.Forms;
using System.ComponentModel;

namespace FormsDesigner.CustomControlFixture;

public sealed class FancyButton : Button
{
	[DefaultValue("Blue")]
	public string Accent { get; set; } = "Blue";
}
