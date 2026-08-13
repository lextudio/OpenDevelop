using System.Collections.Generic;

namespace XamlStudio.Toolkit.Models;

public sealed class XamlRenderSettings
{
    HashSet<XmlnsNamespace> namespaces = new();
    public HashSet<XmlnsNamespace> KnownNamespaces
    {
        get => namespaces;
        set => namespaces = new HashSet<XmlnsNamespace>(value);
    }
    public string ResourceRootPath { get; set; }
    public bool IsBindingDebuggingEnabled { get; set; }
    public bool KeepSuggestedContentSameLength { get; set; } = true;
    public bool IsInitialTemplateValidated { get; set; }
    public object DataContext { get; set; }

    public XamlRenderSettings(IEnumerable<XmlnsNamespace> knownNamespaces = null)
    {
        if (knownNamespaces != null) namespaces = new HashSet<XmlnsNamespace>(knownNamespaces);
    }
}
