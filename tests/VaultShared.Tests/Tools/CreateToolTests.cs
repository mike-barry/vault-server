using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// <c>create</c> over a real tree and a synthetic declaration: both forms, the whole-block
/// validation a supplied note goes through, the basename census and the order it runs in, and the
/// refusals occupancy and the name grammar produce.
///
/// The vocabulary is <see cref="Synthetic"/>'s, so nothing here asserts anything about the vault's
/// real one. What is under test is the tool.
/// </summary>
public sealed class CreateToolTests : IDisposable
{
    private readonly ScratchVault _vault = Scratch();

    public void Dispose() => _vault.Dispose();

    private static ScratchVault Scratch(PathPolicy? policy = null, params string[] opClasses) =>
        new ScratchVault(opClasses: opClasses.Length > 0 ? opClasses : [Synthetic.OpenClass], policy: policy)
            .WithNote("Projects/Widget/Widget.md", "# Widget\n\nSee [[Notes]].\n")
            .WithNote("Reference/Notes.md", "# Notes\n")
            .WithFolder("Projects/Empty")
            .WithFolder("Inbox");

    private static CreateTool Tool() => new(FrontmatterRegistries.Of("scratch", new RegistryState.Loaded(Synthetic.Registry)));

    private CreateResult Create(object arguments) => Create(_vault, arguments);

    private static CreateResult Create(ScratchVault vault, object arguments) =>
        Assert.IsType<CreateResult>(Assert.IsType<ToolResult.Data>(
            VaultToolHarness.Invoke(Tool(), CreateTool.Declaration, vault, arguments)).Value);

    private ToolRefusal Refusal(object arguments) => Refusal(_vault, arguments);

    /// <summary>The refusal as a caller sees it: a <see cref="FrontmatterRefusal"/> travels out in
    /// its own vocabulary and is named through the one table where the envelope is rendered, which
    /// is the step a front-end does and this tool does not.</summary>
    private static ToolRefusal Refusal(ScratchVault vault, object arguments)
    {
        try
        {
            VaultToolHarness.Invoke(Tool(), CreateTool.Declaration, vault, arguments);
        }
        catch (ToolRefusal refusal)
        {
            return refusal;
        }
        catch (FrontmatterRefusal refusal)
        {
            return ToolRefusalEnvelope.Translate(refusal);
        }

        throw new InvalidOperationException("The call answered, and the case expected a refusal.");
    }

    // ---- The declaration ----

    /// <summary>A write floor and no preview gate: a creation destroys nothing, so a plan would echo
    /// the request back.</summary>
    [Fact]
    public void DeclaresAWriteFloorAndMutates()
    {
        Assert.Equal(new Floor.Class("content:write"), CreateTool.Declaration.OpClassFloor);
        Assert.True(CreateTool.Declaration.Mutates);
        Assert.DoesNotContain("apply", NativeToolArguments.SchemaFor(typeof(CreateArguments)).GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    // ---- The note form ----

    [Fact]
    public void CreatesANoteFromFinishedTextAndAnswersItsHash()
    {
        var result = Create(new { path = "Inbox/Thought.md", kind = "note", content = "# Thought\n\nA line.\n" });

        Assert.Equal("Inbox/Thought.md", result.Path);
        Assert.Equal("# Thought\n\nA line.\n", _vault.TextOf("Inbox/Thought.md"));
        Assert.Equal(
            Assert.IsType<ReadOutcome.Content>(VaultFiles.Read(_vault.ToRead("Inbox/Thought.md"))).ContentHash,
            result.ContentHash);
    }

    /// <summary>Nothing is templated and no field is composed: what the caller sent is what
    /// lands, block included.</summary>
    [Fact]
    public void ASuppliedBlockIsStoredAsTheNotesOwnFrontmatter()
    {
        Create(new
        {
            path = "Inbox/Fenced.md",
            kind = "note",
            content = "---\ndoc.title: Fenced\ndoc.type: note\n---\n# Fenced\n",
        });

        var split = NoteSplit.Of(_vault.TextOf("Inbox/Fenced.md"));
        Assert.True(split.HasBlock);
        Assert.Equal("doc.title: Fenced\ndoc.type: note", split.InnerText);
        Assert.Equal("# Fenced\n", split.Body);
    }

    /// <summary>The supplied block is validated whole, because the caller supplied every key - so an
    /// undeclared key is a refusal rather than something passed through.</summary>
    [Fact]
    public void ASuppliedBlockIsValidatedWholeRatherThanKeyByKey()
    {
        var refusal = Refusal(new { path = "Inbox/Bad.md", kind = "note", content = "---\nmade.up: x\n---\n# Bad\n" });

        Assert.Equal(ToolCodes.UnknownKey, refusal.Code);
        Assert.False(_vault.HasFile("Inbox/Bad.md"));
    }

    /// <summary>Authority is classified over the outcome. Against no original the diff counts every
    /// key present as changed, so a key the declaration prices is priced on creation too.</summary>
    [Fact]
    public void AKeyRequiringElevationIsAssertedAndReported()
    {
        using var denied = Scratch(opClasses: Synthetic.OpenClass);
        var refusal = Refusal(denied, new
        {
            path = "Inbox/Agentic.md",
            kind = "note",
            content = "---\nagent.mode: auto\n---\n# Agentic\n",
        });

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.False(denied.HasFile("Inbox/Agentic.md"));

        using var granted = Scratch(opClasses: [Synthetic.OpenClass, Synthetic.ElevatedClass]);
        var result = Create(granted, new
        {
            path = "Inbox/Agentic.md",
            kind = "note",
            content = "---\nagent.mode: auto\n---\n# Agentic\n",
        });

        Assert.Contains(Synthetic.ElevatedClass, result.AssertedAuthority);
    }

    /// <summary>The path policy prices the target as well, and creation routes through the write
    /// path that consults it.</summary>
    [Fact]
    public void ThePathPolicyPricesTheTargetOfACreation()
    {
        var policy = Synthetic.Policy($$"""[ { "path": "Areas/Agentic", "mutation_requires": "{{Synthetic.ElevatedClass}}" } ]""");

        using var denied = Scratch(policy, Synthetic.OpenClass).WithFolder("Areas/Agentic");
        var refusal = Refusal(denied, new { path = "Areas/Agentic/Note.md", kind = "note", content = "# Note\n" });

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.Equal(
            ["Areas/Agentic/Note.md"],
            System.Text.Json.JsonDocument.Parse(VaultToolHarness.Data(refusal)!["paths"]).RootElement.EnumerateArray().Select(v => v.GetString()!));
        Assert.False(denied.HasFile("Areas/Agentic/Note.md"));

        using var granted = Scratch(policy, Synthetic.OpenClass, Synthetic.ElevatedClass).WithFolder("Areas/Agentic");
        Assert.Contains(Synthetic.ElevatedClass, Create(granted, new { path = "Areas/Agentic/Note.md", kind = "note", content = "# Note\n" }).AssertedAuthority);
    }

    // ---- The basename census ----

    /// <summary>A name nothing else carries changes what no link resolves to, so no pass runs.
    /// <b>Absent is not empty</b>: this is the difference the nullable member exists for.</summary>
    [Fact]
    public void AUniqueBasenameRunsNoPassAndReportsNoPinnedMember()
    {
        Assert.Null(Create(new { path = "Inbox/Unheard-Of.md", kind = "note", content = "# New\n" }).PinnedLinks);
    }

    /// <summary>A duplicated basename runs the pass. It may still pin nothing, and an empty list is
    /// that finding rather than an absence.</summary>
    [Fact]
    public void ADuplicatedBasenameRunsThePassAndReportsWhatItPinned()
    {
        var result = Create(new { path = "Projects/Widget/Notes.md", kind = "note", content = "# Notes\n" });

        var pinned = Assert.IsAssignableFrom<IReadOnlyList<VaultShared.Links.LinkRewrite>>(result.PinnedLinks);
        Assert.Equal(["Projects/Widget/Widget.md:3 Notes -> Reference/Notes"], pinned.Select(p => $"{p.Path}:{p.Line} {p.From} -> {p.To}"));
        Assert.Contains("[[Reference/Notes]]", _vault.TextOf("Projects/Widget/Widget.md"));
    }

    /// <summary>
    /// The order is the whole of the guarantee. Interrupted after pinning, the vault holds links
    /// naming a target that is still correct; interrupted after writing, it holds bare links that
    /// have silently changed meaning - so a creation that cannot write its note must still have
    /// pinned first.
    /// </summary>
    [Fact]
    public void PinsAreAppliedBeforeTheNoteIsWritten()
    {
        // The destination's folder is gone, so the note cannot be created; the pass runs against a
        // basename that is already in use and its rewrite has to have landed regardless.
        var refusal = Refusal(new { path = "Nowhere/Notes.md", kind = "note", content = "# Notes\n" });

        Assert.Equal(ToolCodes.ParentMissing, refusal.Code);
        Assert.False(_vault.HasFile("Nowhere/Notes.md"));
        Assert.Contains("[[Reference/Notes]]", _vault.TextOf("Projects/Widget/Widget.md"));
    }

    /// <summary>Creating at a path that already exists plans no pin at all: the note is already one
    /// of the candidates, so no link's resolution changes and the refusal is clean.</summary>
    [Fact]
    public void AnOccupiedTargetIsRefusedAndPinsNothingOnTheWay()
    {
        var before = _vault.TextOf("Projects/Widget/Widget.md");
        var refusal = Refusal(new { path = "Reference/Notes.md", kind = "note", content = "# Replaced\n" });

        Assert.Equal(ToolCodes.AlreadyExists, refusal.Code);
        Assert.Equal("# Notes\n", _vault.TextOf("Reference/Notes.md"));
        Assert.Equal(before, _vault.TextOf("Projects/Widget/Widget.md"));
    }

    // ---- The folder form ----

    [Fact]
    public void CreatesAFolderAndDescribesNoContent()
    {
        var result = Create(new { path = "Projects/Gizmo", kind = "folder" });

        Assert.True(_vault.HasFolder("Projects/Gizmo"));
        Assert.Null(result.ContentHash);
        // A folder brings no note into existence, so no pass ran and the member is absent.
        Assert.Null(result.PinnedLinks);
    }

    /// <summary>The placeholder is reached from the operand and never named by the caller: the
    /// perimeter refuses a dot segment and the grammar refuses the spelling, both deliberately.</summary>
    [Fact]
    public void GitKeepSeedsThePlaceholderTheCallerCannotName()
    {
        Create(new { path = "Projects/Held", kind = "folder", git_keep = true });

        Assert.True(_vault.HasFile("Projects/Held/.gitkeep"));
        Assert.Empty(_vault.BytesOf("Projects/Held/.gitkeep"));
    }

    [Fact]
    public void GitKeepIsOffByDefault()
    {
        Create(new { path = "Projects/Bare", kind = "folder" });

        Assert.True(_vault.HasFolder("Projects/Bare"));
        Assert.False(_vault.HasFile("Projects/Bare/.gitkeep"));
    }

    /// <summary>
    /// A caller still cannot name it, from either direction. As a note it carries no markdown
    /// extension and never reaches the layer; as a folder it reaches the layer and meets the operand
    /// perimeter, which refuses a dot segment whatever the grant. That is what makes internal reach
    /// derived from the operand rather than caller-directed.
    /// </summary>
    [Fact]
    public void ACallerCannotNameThePlaceholderItself()
    {
        Assert.Equal(ToolCodes.InvalidPath, Refusal(new { path = "Projects/Held/.gitkeep", kind = "note", content = "" }).Code);
        Assert.Equal(ToolCodes.PathExcluded, Refusal(new { path = "Projects/Held/.gitkeep", kind = "folder" }).Code);
    }

    /// <summary>A folder creation consults the vault's path policy itself: it routes through no
    /// frontmatter write, so nothing else would.</summary>
    [Fact]
    public void ThePathPolicyPricesTheTargetOfAFolderCreation()
    {
        var policy = Synthetic.Policy($$"""[ { "path": "Areas/Agentic", "mutation_requires": "{{Synthetic.ElevatedClass}}" } ]""");
        using var denied = Scratch(policy, Synthetic.OpenClass).WithFolder("Areas/Agentic");

        var refusal = Refusal(denied, new { path = "Areas/Agentic/Sub", kind = "folder" });

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.False(denied.HasFolder("Areas/Agentic/Sub"));
    }

    // ---- Names and paths ----

    /// <summary>The name grammar's refusal names a conforming spelling where one can be derived, so
    /// the miss costs one retry.</summary>
    [Fact]
    public void ANameOutsideTheGrammarIsRefusedNamingASpellingThatWouldWork()
    {
        var refusal = Refusal(new { path = "Inbox/new thought.md", kind = "note", content = "# X\n" });

        Assert.Equal(ToolCodes.InvalidName, refusal.Code);
        Assert.Equal("New-thought.md", VaultToolHarness.Data(refusal)!["suggested"]);
        Assert.False(_vault.HasFile("Inbox/new thought.md"));
    }

    /// <summary>The extension is never inferred, and a note-kind call handed an extensionless path
    /// is told the spelling that would have worked.</summary>
    [Fact]
    public void ANoteKindCallOnAnExtensionlessPathIsRefusedWithTheSpelling()
    {
        var refusal = Refusal(new { path = "Inbox/Thought", kind = "note", content = "" });

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Inbox/Thought.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>Read the other way: a folder operand carries no extension, and the disagreement
    /// between the spelling and the assertion is the kind refusal.</summary>
    [Fact]
    public void AFolderKindCallOnANotePathIsTheKindRefusal()
    {
        Assert.Equal(ToolCodes.WrongKind, Refusal(new { path = "Projects/Gizmo.md", kind = "folder" }).Code);
    }

    [Fact]
    public void AMissingParentIsNamedInVaultRelativeTerms()
    {
        var refusal = Refusal(new { path = "Nowhere/Deep/Thought.md", kind = "note", content = "" });

        Assert.Equal(ToolCodes.ParentMissing, refusal.Code);
        Assert.Equal("Nowhere/Deep", VaultToolHarness.Data(refusal)!["parent"]);
    }
}
