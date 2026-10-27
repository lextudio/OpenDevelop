using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.SharpDevelop.Startup;
using Xunit;

namespace OpenDevelop.XamlDom.Tests.LibreWPF;

public sealed class PortableTextCompatibilityTests
{
    [Theory]
    [InlineData("Ideal", "Project Options")]
    [InlineData("Display", "Project Options")]
    [InlineData("Ideal", "项目选项")]
    [InlineData("Display", "项目选项")]
    public void TextBlock_MeasuresWithoutDroppingText(string mode, string text)
    {
        // WPF elements require an STA thread on Windows; xUnit v3 runs theories on the MTA pool.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Measure(mode, text); }
            catch (Exception ex) { failure = ex; }
        });
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    static void Measure(string mode, string text)
    {
        PortableTextCompatibility.Initialize();
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        TextOptions.SetTextFormattingMode(block, Enum.Parse<TextFormattingMode>(mode));
        block.Measure(new Size(80, double.PositiveInfinity));
        Assert.True(block.DesiredSize.Width > 0);
        Assert.True(block.DesiredSize.Height > 0);
        Assert.Equal(text, block.Text);
    }
}
