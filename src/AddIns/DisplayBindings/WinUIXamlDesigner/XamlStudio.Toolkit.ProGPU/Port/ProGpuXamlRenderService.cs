using System;
using System.Linq;
using System.Xml.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using XamlStudio.Toolkit.Models;

namespace XamlStudio.Toolkit.Services;

/// <summary>
/// ProGPU execution boundary for XAML Studio. ProGPU materializes XAML through its compiler and
/// collectible preview assembly pipeline; it intentionally does not emulate WinUI XamlReader.
/// </summary>
public interface IProGpuXamlExecutor
{
    Task<object> MaterializeAsync(string xaml);
}

public partial class XamlRenderService
{
    readonly IProGpuXamlExecutor executor;

    public XamlRenderService(IProGpuXamlExecutor executor)
    {
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        XamlBindingWrapperManager.Instance.Register(Id, this);
    }

    public async Task<XamlRenderResultContext> RenderAsync(string content, XamlRenderSettings settings = null)
    {
        settings ??= new XamlRenderSettings();
        XamlBindingWrapperManager.Instance.Clear(Id);
        var result = new XamlRenderResultContext(content) { Bindings = Enumerable.Empty<XamlBindingInfo>() };
        await AppAssemblyInfo.Instance.InitializeAsync();
        PreProcessXmlns(ref result, ref settings);
        if (!ReadXmlTree(ref result)) return result;

        // Upstream strips x:Class because XAML Studio renders classless markup through WinUI's
        // runtime XamlReader. ProGPU has no runtime reader: its preview host synthesizes a partial
        // class and therefore *requires* x:Class. Capture it before GetBindings deletes it and put
        // it back afterwards, so upstream's binding/diagnostic behaviour stays unforked.
        var classDirective = XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml");
        var className = result.Document?.Root?.Attribute(classDirective)?.Value;

        GetBindings(result, settings.IsBindingDebuggingEnabled);

        if (className != null && result.Document?.Root != null)
        {
            result.Document.Root.SetAttributeValue(classDirective, className);
            result.RenderedContent = Serialize(result.Document);
        }

        try
        {
            result.Element = await executor.MaterializeAsync(result.RenderedContent);
            if (result.Element is FrameworkElement element && settings.DataContext != null)
                element.DataContext = settings.DataContext;
        }
        catch (Exception exception)
        {
            result.Errors.Add(new XamlExceptionRange(
                exception.GetBaseException().Message, exception, 1, 1, string.Empty));
        }

        result.Bindings = XamlBindingWrapperManager.Instance.GetBindings(Id);
        return result;
    }

    /// <summary>Matches upstream's own serialization in GetBindings, including its XML-declaration trim.</summary>
    static string Serialize(System.Xml.Linq.XDocument document)
    {
        using var writer = new System.IO.StringWriter();
        document.Save(writer, System.Xml.Linq.SaveOptions.DisableFormatting);
        var text = writer.ToString();
        var start = text.IndexOf('<', 1);
        return start < 0 ? text : text.Substring(start);
    }

    static string GetLine(string content, uint lineNumber)
    {
        if (lineNumber < 1 || string.IsNullOrEmpty(content)) return string.Empty;
        var lines = content.Split('\n');
        return lineNumber > lines.Length ? string.Empty : lines[lineNumber - 1];
    }
}
