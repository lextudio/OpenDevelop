namespace XsgLocalFeedFixture;

// New-style XAML (no xmlns at all): the XAML source generator supplies the default WPF namespaces while LibreWpfSimplerXaml is on, which it is
// for a project that enabled the generator. The two other windows declare their namespaces (old style); both kinds go through the same generator.
public partial class ThirdWindow : System.Windows.Window
{
    public ThirdWindow() => InitializeComponent();
}
