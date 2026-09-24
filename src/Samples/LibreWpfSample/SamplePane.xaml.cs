using System.Windows.Controls;

namespace sample;

public partial class SamplePane : UserControl
{
    public SamplePane()
    {
        InitializeComponent();
    }

    public int GetTitleHashCode()
    {
        return PaneTitle.GetHashCode();
    }

    public string GetTitleText()
    {
        return PaneTitle.Text;
    }

    public int GetBodyHashCode()
    {
        return PaneBody.GetHashCode();
    }

    public string GetBodyText()
    {
        return PaneBody.Text;
    }

    public int GetListItemOneHashCode()
    {
        return PaneListItemOne.GetHashCode();
    }

    public string GetListItemOneText()
    {
        return PaneListItemOne.Text;
    }

    public int GetListItemTwoHashCode()
    {
        return PaneListItemTwo.GetHashCode();
    }

    public string GetListItemTwoText()
    {
        return PaneListItemTwo.Text;
    }

    public int GetListSelectedIndex()
    {
        return PaneList.SelectedIndex;
    }
}
