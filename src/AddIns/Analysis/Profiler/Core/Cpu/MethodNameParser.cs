namespace OpenDevelop.Profiler.Cpu;

/// <summary>The declaring type and method a profiler frame name points at, as far as it can be recovered.</summary>
/// <param name="TypeName">Full type name without generic arguments, e.g. <c>MyApp.Services.Worker</c>.</param>
/// <param name="MethodName">The method, or the method a lambda or local function was declared in.</param>
/// <param name="ParameterCount">The parameter count, or <c>null</c> when it cannot be trusted (compiler-generated methods).</param>
public sealed record MemberReference(string TypeName, string MethodName, int? ParameterCount);

/// <summary>
/// Turns a sampled frame name such as <c>MyApp.Worker.Run(string, int)</c> into something a symbol search can
/// look up. Best effort: names of lambdas and local functions map back to the method that contains them.
/// </summary>
public static class MethodNameParser
{
    public static MemberReference? Parse(string frameName)
    {
        if (string.IsNullOrWhiteSpace(frameName))
            return null;

        var text = frameName.Trim();
        var open = IndexAtDepthZero(text, '(');
        var head = open < 0 ? text : text[..open];
        string? arguments = null;
        if (open >= 0)
        {
            var close = text.LastIndexOf(')');
            arguments = text[(open + 1)..(close > open ? close : text.Length)];
        }

        // A constructor's name starts with a dot of its own: "Ns.Type..ctor".
        string typePart, methodPart;
        var ctor = head.LastIndexOf("..c", StringComparison.Ordinal);
        if (ctor > 0 && head[(ctor + 1)..] is ".ctor" or ".cctor")
        {
            typePart = head[..ctor];
            methodPart = head[(ctor + 1)..];
        }
        else
        {
            var dot = LastIndexAtDepthZero(head, '.');
            if (dot <= 0 || dot == head.Length - 1)
                return null;
            typePart = head[..dot];
            methodPart = head[(dot + 1)..];
        }

        var typeName = StripGenericArguments(typePart);
        var generated = methodPart.StartsWith('<') || methodPart.Contains("g__", StringComparison.Ordinal);
        var methodName = generated ? ContainingMethod(methodPart) : methodPart;
        if (typeName.Length == 0 || methodName.Length == 0)
            return null;

        return new MemberReference(typeName, methodName, generated ? null : CountParameters(arguments));
    }

    // "<<Main>$>g__HotLeaf|0_1" and "<Run>b__3_0" both start with the angle-bracketed name of the method that
    // declares them; unwrap nested brackets down to it.
    static string ContainingMethod(string generatedName)
    {
        var name = generatedName;
        while (name.StartsWith('<'))
        {
            var depth = 0;
            var end = -1;
            for (var i = 0; i < name.Length && end < 0; i++)
            {
                if (name[i] == '<')
                    depth++;
                else if (name[i] == '>' && --depth == 0)
                    end = i;
            }
            if (end < 0)
                break;
            name = name[1..end];
        }
        return name.TrimEnd('$');
    }

    static string StripGenericArguments(string typeName)
    {
        var bracket = typeName.IndexOf('[');
        return (bracket < 0 ? typeName : typeName[..bracket]).Trim();
    }

    static int? CountParameters(string? arguments)
    {
        if (arguments is null)
            return null;
        if (string.IsNullOrWhiteSpace(arguments))
            return 0;
        var count = 1;
        var depth = 0;
        foreach (var c in arguments)
        {
            if (c is '[' or '<' or '(')
                depth++;
            else if (c is ']' or '>' or ')')
                depth--;
            else if (c == ',' && depth == 0)
                count++;
        }
        return count;
    }

    static int IndexAtDepthZero(string text, char target)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '[' || text[i] == '<')
                depth++;
            else if (text[i] == ']' || text[i] == '>')
                depth--;
            else if (text[i] == target && depth == 0)
                return i;
        }
        return -1;
    }

    static int LastIndexAtDepthZero(string text, char target)
    {
        var depth = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == ']' || text[i] == '>')
                depth++;
            else if (text[i] == '[' || text[i] == '<')
                depth--;
            else if (text[i] == target && depth == 0)
                return i;
        }
        return -1;
    }
}
