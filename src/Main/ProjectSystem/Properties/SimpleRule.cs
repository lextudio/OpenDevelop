// Clean-room stub. See docs/project-system.md.

namespace Microsoft.VisualStudio.ProjectSystem.Properties;

/// <summary>
/// Lightweight immutable browse-object rule for CPS tree nodes that do not yet
/// have a full property-page catalog backing them.
/// </summary>
public sealed class SimpleRule : IRule
{
    public SimpleRule(string schemaName, string? itemType, string? itemName)
    {
        Name = schemaName;
        ItemType = itemType;
        ItemName = itemName;
        Schema = new SimpleRuleSchema(schemaName);
    }

    public string Name { get; }
    public string? ItemType { get; }
    public string? ItemName { get; }
    public IRuleSchema Schema { get; }

    private sealed class SimpleRuleSchema : IRuleSchema
    {
        public SimpleRuleSchema(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }
}
