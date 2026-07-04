namespace Microsoft.VisualStudio.Text;

internal readonly struct LazyStringSplit : IEnumerable<string>
{
    private readonly string _text;
    private readonly char _separator;

    public LazyStringSplit(string text, char separator)
    {
        _text = text;
        _separator = separator;
    }

    public IEnumerator<string> GetEnumerator()
    {
        return ((IEnumerable<string>)_text.Split(_separator)).GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
