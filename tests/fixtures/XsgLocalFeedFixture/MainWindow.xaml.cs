using System.Windows;

namespace XsgLocalFeedFixture;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    // Loaded fires again on every tree InitializeComponent builds for the live window, so which
    // handler the XAML names is observable through Marker without any input injection. The
    // Hot Reload end-to-end test switches the XAML between the two.
    void OnLoadedA(object sender, RoutedEventArgs e) => Marker!.Text = "handler A";

    void OnLoadedB(object sender, RoutedEventArgs e) => Marker!.Text = "handler B";
}
