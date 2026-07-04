using System.Text;

namespace Microsoft.VisualStudio.Buffers.PooledObjects;

internal sealed class PooledStringBuilder
{
    private readonly StringBuilder _builder = new();

    private PooledStringBuilder()
    {
    }

    public static PooledStringBuilder GetInstance()
    {
        return new PooledStringBuilder();
    }

    public void Append(string? value)
    {
        _builder.Append(value);
    }

    public void Append(char value)
    {
        _builder.Append(value);
    }

    public string ToStringAndFree()
    {
        return _builder.ToString();
    }
}
