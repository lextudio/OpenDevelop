using System;
using ICSharpCode.SharpDevelop.Commands;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class LinkCommandTests : IDisposable
{
    readonly Action<string> _original = LinkCommand.WebLauncher;

    public void Dispose() => LinkCommand.WebLauncher = _original;

    [Theory]
    [InlineData("http://www.mono-project.com/")]
    [InlineData("https://github.com/lextudio/OpenDevelop")]
    [InlineData("HTTPS://example.org/")]
    public void WebAddress_IsOpenedInTheBrowser_NotAsAFile(string url)
    {
        string? opened = null;
        LinkCommand.WebLauncher = u => opened = u;

        new LinkCommand(url).Run();

        Assert.Equal(url, opened);
    }
}
