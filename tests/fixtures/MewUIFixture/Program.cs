using Aprillz.MewUI;

namespace MewUIFixture;

public static class Program
{
    public static int Main()
    {
        var app = Application.Create();
        app.Run(new Windows.MainWindow());
        return 0;
    }
}
