// Export/Import/ImportMany subclass the REAL MEF2 attribute types (System.Composition,
// MIT-licensed, part of the .NET runtime's own composition attribute model) rather than
// reimplementing them. This lets the real Microsoft.VisualStudio.Composition engine
// (AttributedPartDiscovery et al. — also real, MIT, github.com/microsoft/vs-mef) genuinely
// discover and compose parts attributed with these wrappers, since .NET attribute reflection
// (GetCustomAttribute<T>) matches subclasses. Declared here (rather than used directly via
// `using System.Composition;`) purely so `using Microsoft.VisualStudio.Composition;` — what
// upstream dotnet/project-system files actually import — resolves the same unqualified
// [Export]/[Import]/[ImportMany] names. See docs/project-system.md (Slice 44).
//
// ImportingConstructorAttribute is NOT wrapped here: the real System.Composition type is
// sealed, so it can't be subclassed. Real VS MEF's constructor selection requires the exact
// sealed type, so ProjectSystemManaged/GlobalUsings.cs instead aliases the bare name directly
// to System.Composition.ImportingConstructorAttribute for the files compiled into that project.

using System;

namespace Microsoft.VisualStudio.Composition;

/// <summary>Marks a type or member as a MEF export. Subclasses the real System.Composition.ExportAttribute.</summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = true, Inherited = false)]
public sealed class ExportAttribute : System.Composition.ExportAttribute
{
    public ExportAttribute() { }
    public ExportAttribute(string contractName) : base(contractName) { }
    public ExportAttribute(Type contractType) : base(contractType) { }
    public ExportAttribute(string contractName, Type contractType) : base(contractName, contractType) { }
}

/// <summary>Marks a constructor or member as a MEF import. Subclasses the real System.Composition.ImportAttribute.</summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false, Inherited = false)]
public sealed class ImportAttribute : System.Composition.ImportAttribute
{
    public ImportAttribute() { }
    public ImportAttribute(string contractName) : base(contractName) { }
    public ImportAttribute(Type contractType) => ContractType = contractType;

    /// <remarks>Not on the real base type — <c>[Import(typeof(X))]</c> is a CPS convention for
    /// "any single export of type X" that this shim doesn't route through contract-name matching.</remarks>
    public Type? ContractType { get; }
}

/// <summary>Marks a field or property for collection of all MEF exports matching the contract. Subclasses the real System.Composition.ImportManyAttribute.</summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false, Inherited = false)]
public sealed class ImportManyAttribute : System.Composition.ImportManyAttribute
{
    public ImportManyAttribute() { }
    public ImportManyAttribute(string contractName) : base(contractName) { }
}

// ── CPS SDK attributes ────────────────────────────────────────────────────────────────────────

/// <summary>
/// Restricts a MEF component to projects that have the specified capability expression.
/// Reconstructed from MIT dotnet/project-system usage.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property,
    AllowMultiple = false, Inherited = false)]
public class AppliesToAttribute : Attribute
{
    public AppliesToAttribute(string appliesTo) { AppliesTo = appliesTo; }
    public string AppliesTo { get; }
}

/// <summary>
/// Sets the order precedence of a MEF component.
/// Reconstructed from MIT dotnet/project-system usage.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property,
    AllowMultiple = false, Inherited = false)]
public class OrderAttribute : Attribute
{
    public OrderAttribute() { }
    public OrderAttribute(int order) { Order = order; }
    public int Order { get; }
}
