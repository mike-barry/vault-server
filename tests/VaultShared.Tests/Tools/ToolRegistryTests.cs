using System.Text.Json;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the registry both front-ends read their tool list from. A registration pairs a declaration
/// with a tool instance, so a type that implements <see cref="ITool"/> alone cannot be registered
/// and a tool's name, description, argument record and flags are stated in one place.
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

    /// <summary>For an ordinary tool, the advertised schema is the one derived from the argument
    /// record its declaration names.</summary>
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
    /// A tool that builds its own schema from a vault's frontmatter declaration is asked for it and
    /// handed the vault name. A front-end therefore never has to special-case a tool by name.
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

    /// <summary>Names are matched exactly. A tool name is a wire name, so a different case is a
    /// different name.</summary>
    [Fact]
    public void Find_UnregisteredName_AnswersNull()
    {
        var registry = new ToolRegistry().Add(SyntheticTools.Declaration("probe"), new RecordingTool());

        Assert.Null(registry.Find("no_such_tool"));
        Assert.Null(registry.Find("Probe"));
    }

    /// <summary>Registering two tools under one name throws. It is a wiring mistake, and there is no
    /// precedence rule for it.</summary>
    [Fact]
    public void Add_DuplicateName_Throws()
    {
        var registry = new ToolRegistry().Add(SyntheticTools.Declaration("probe"), new RecordingTool());

        Assert.Throws<ArgumentException>(() => registry.Add(SyntheticTools.Declaration("probe"), new RecordingTool()));
    }
}
