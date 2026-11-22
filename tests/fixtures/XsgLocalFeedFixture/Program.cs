using System.Windows;

namespace XsgLocalFeedFixture;

/// <summary>
/// Code-only entry point: an App.xaml would need the BAML compiler, which this fixture disables so
/// that the XAML source generator is provably the only XAML compiler.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main() => new Application().Run(new MainWindow());
}
