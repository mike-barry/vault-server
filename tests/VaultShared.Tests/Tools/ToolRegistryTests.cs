using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// The registry both front-ends project from. It holds declarations, so a type implementing
/// <see cref="ITool"/> and nothing else has nothing to register - which is what keeps a tool's name,
/// description, argument record and flags stated exactly once.
/// </summary>
public sealed class ToolRegistryTests
{
    [Fact]
    public void Declarations_AreListedInRegistrationOrder()
    {
        var registry = new ToolRegistry()
            .Add(SyntheticTools.Declaration("first"), new RecordingTool())
            .Add(SyntheticTools.Declaration("second"), new RecordingTool());

        Assert.Equal(["first", "second"], registry.Declarations.Select(d => d.Name));
    }

    [Fact]
    public void Find_AnsersTheDeclarationAndTheInstanceRegisteredUnderIt()
    {
        var tool = new RecordingTool();
        var declaration = SyntheticTools.Declaration("probe");
        var registry = new ToolRegistry().Add(declaration, tool);

        var registration = registry.Find("probe");
        Assert.NotNull(registration);
        Assert.Same(declaration, registration.Value.Declaration);
        Assert.Same(tool, registration.Value.Tool);
    }

    /// <summary>Matched ordinally: a tool name is a wire name, and two spellings are two names.</summary>
    [Fact]
    public void Find_UnregisteredName_AnswersNull()
    {
        var registry = new ToolRegistry().Add(SyntheticTools.Declaration("probe"), new RecordingTool());

        Assert.Null(registry.Find("no_such_tool"));
        Assert.Null(registry.Find("Probe"));
    }

    /// <summary>Two tools under one name is a wiring defect, not a precedence rule to invent.</summary>
    [Fact]
    public void Add_DuplicateName_Throws()
    {
        var registry = new ToolRegistry().Add(SyntheticTools.Declaration("probe"), new RecordingTool());

        Assert.Throws<ArgumentException>(() => registry.Add(SyntheticTools.Declaration("probe"), new RecordingTool()));
    }
}
