using TeamBeats.Modules.Agents;

namespace TeamBeats.Testing;

/// <summary>
/// Forced template security updates a test names (spec 020 FR-005). The product ships none yet, so tests set their own;
/// a test that sets some clears them when it ends, since the factory is shared by the tests of the collection.
/// </summary>
public sealed class TestTemplateSecurityUpdates : ITemplateSecurityUpdates
{
    private volatile IReadOnlyList<TemplateSecurityUpdate> updates = [];

    public IReadOnlyList<TemplateSecurityUpdate> All => updates;

    public void Set(params TemplateSecurityUpdate[] list)
    {
        TemplateSecurity.Check(list);
        updates = list;
    }

    public void Clear() => updates = [];
}
