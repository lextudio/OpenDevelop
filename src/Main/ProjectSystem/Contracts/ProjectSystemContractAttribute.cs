// Clean-room stubs for CPS MEF contract attributes.
// These come from the proprietary Microsoft.VisualStudio.ProjectSystem.SDK.
// We reproduce only the attribute shape so upstream MIT code compiles unchanged.
// See docs/project-system.md.

using System;

namespace Microsoft.VisualStudio.ProjectSystem;

public enum ProjectSystemContractScope
{
    Global,
    UnconfiguredProject,
    ConfiguredProject,
}

public enum ProjectSystemContractProvider
{
    System,
    Private,
    Extension,
}

public enum ImportCardinality
{
    ZeroOrMore,
    ZeroOrOne,
    ExactlyOne,
}

/// <summary>
/// Stub for the CPS SDK's <c>[ProjectSystemContract]</c> attribute.
/// Decorates interfaces that participate in the CPS MEF composition model.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, AllowMultiple = true)]
public sealed class ProjectSystemContractAttribute : Attribute
{
    public ProjectSystemContractAttribute() { }

    public ProjectSystemContractAttribute(
        ProjectSystemContractScope scope,
        ProjectSystemContractProvider provider = ProjectSystemContractProvider.System,
        ImportCardinality cardinality = ImportCardinality.ExactlyOne)
    {
        Scope    = scope;
        Provider = provider;
        Cardinality = cardinality;
    }

    public ProjectSystemContractScope Scope { get; set; }
    public ProjectSystemContractProvider Provider { get; set; }
    public ImportCardinality Cardinality { get; set; }
}
