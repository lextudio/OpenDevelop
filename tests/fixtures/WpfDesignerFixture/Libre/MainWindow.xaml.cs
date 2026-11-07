using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace sample;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        HotReloadReady();
    }

    private void HotReloadReady()
    {
    }

    public int GetPaneHashCode()
    {
        return MainPane.GetHashCode();
    }

    public double GetPaneWidth()
    {
        return MainPane.Width;
    }

    public int GetPaneTitleHashCode()
    {
        return MainPane.GetTitleHashCode();
    }

    public string GetPaneTitleText()
    {
        return MainPane.GetTitleText();
    }

    public int GetPaneBodyHashCode()
    {
        return MainPane.GetBodyHashCode();
    }

    public string GetPaneBodyText()
    {
        return MainPane.GetBodyText();
    }

    public int GetPaneListItemOneHashCode()
    {
        return MainPane.GetListItemOneHashCode();
    }

    public string GetPaneListItemOneText()
    {
        return MainPane.GetListItemOneText();
    }

    public int GetPaneListItemTwoHashCode()
    {
        return MainPane.GetListItemTwoHashCode();
    }

    public string GetPaneListItemTwoText()
    {
        return MainPane.GetListItemTwoText();
    }

    public int GetPaneListSelectedIndex()
    {
        return MainPane.GetListSelectedIndex();
    }
}
