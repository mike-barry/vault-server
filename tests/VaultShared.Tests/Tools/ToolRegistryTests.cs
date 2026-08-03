using System.Text.Json;
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

        Assert.Equal(["first", "second"], registry.Registrations.Select(r => r.Declaration.Name));
    }

    /// <summary>The schema a front-end advertises for an ordinary tool is the one derived from the
    /// argument record its declaration names.</summary>
    [Fact]
    public void AdvertisedSchema_OrdinaryTool_IsTheArgumentRecordsSchema()
    {
        var declaration = SyntheticTools.Declaration("probe");
        var registration = new ToolRegistry().Add(declaration, new RecordingTool()).Find("probe")!.Value;

        Assert.Equal(
            NativeToolArguments.SchemaFor(declaration.Arguments).GetRawText(),
            registration.AdvertisedSchema("any-vault").GetRawText());
    }

    /// <summary>
    /// The vocabulary seam: a tool that renders its own schema over a vault's declaration is asked
    /// for it, and is handed the vault the projection chose. This is the whole of what keeps a
    /// front-end from naming <c>edit</c> - the type answers, not a branch on the name.
    /// </summary>
    [Fact]
    public void AdvertisedSchema_VocabularyTool_IsTheToolsOwnRenderingForTheNamedVault()
    {
        var tool = new VocabularyTool();
        var registration = new ToolRegistry().Add(SyntheticTools.Declaration("probe"), tool).Find("probe")!.Value;

        var schema = registration.AdvertisedSchema("chosen");

        Assert.Equal("chosen", tool.AskedFor);
        Assert.Equal("chosen", schema.GetProperty("vault").GetString());
    }

    private sealed class VocabularyTool : ITool, IVocabularySchema
    {
        public string? AskedFor { get; private set; }

        public JsonElement AdvertisedSchema(string vaultName)
        {
            AskedFor = vaultName;
            return JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["vault"] = vaultName });
        }

        public ToolResult Invoke(ToolCall call) => new ToolResult.Content("");
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
