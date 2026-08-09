using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the <c>create</c> tool against a real directory tree and a synthetic frontmatter
/// declaration: both the note and folder forms, the validation a supplied frontmatter block goes
/// through, the basename census and the order it runs in, and the refusals raised by an occupied
/// target or a name outside the grammar.
///
/// The frontmatter vocabulary is <see cref="Synthetic"/>'s, so no test here depends on the real
/// vault's registry.
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

    /// <summary>Invokes the tool and returns the refusal a caller would see. A
    /// <see cref="FrontmatterRefusal"/> is translated into a <see cref="ToolRefusal"/> here because
    /// the tool does not do that itself; a front-end does it when it renders the envelope.</summary>
    /// <param name="vault">The scratch vault to run the call against.</param>
    /// <param name="arguments">The tool arguments, as an anonymous object.</param>
    /// <returns>The refusal the call produced.</returns>
    /// <exception cref="InvalidOperationException">The call succeeded instead of refusing.</exception>
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

    /// <summary>The declaration asks for a write floor and advertises no <c>apply</c> property. A
    /// creation destroys nothing, so there is nothing for a preview to show.</summary>
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

    /// <summary>Nothing is templated and no field is composed. The content the caller sent is what
    /// lands on disk, frontmatter block included.</summary>
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

    /// <summary>The supplied block is validated as a whole, since the caller wrote every key in it.
    /// An undeclared key is refused rather than passed through.</summary>
    [Fact]
    public void ASuppliedBlockIsValidatedWholeRatherThanKeyByKey()
    {
        var refusal = Refusal(new { path = "Inbox/Bad.md", kind = "note", content = "---\nmade.up: x\n---\n# Bad\n" });

        Assert.Equal(ToolCodes.UnknownKey, refusal.Code);
        Assert.False(_vault.HasFile("Inbox/Bad.md"));
    }

    /// <summary>Authority is classified over the resulting frontmatter. There is no original to
    /// diff against, so every key present counts as changed and a key the declaration requires
    /// elevation for is charged on creation as well.</summary>
    [Fact]
    public void AKeyRequiringElevationIsAssertedAndReported()
    {
        // A key name is taken from the parsed block rather than from the text the caller wrote, so a
        // key spelled in quotes is the same key and is charged the same elevation.
        foreach (var content in new[] { "---\nagent.mode: auto\n---\n# Agentic\n", "---\n\"agent.mode\": auto\n---\n# Agentic\n" })
        {
            using var denied = Scratch(opClasses: Synthetic.OpenClass);
            var refusal = Refusal(denied, new { path = "Inbox/Agentic.md", kind = "note", content });

            Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
            Assert.False(denied.HasFile("Inbox/Agentic.md"));

            using var granted = Scratch(opClasses: [Synthetic.OpenClass, Synthetic.ElevatedClass]);
            var result = Create(granted, new { path = "Inbox/Agentic.md", kind = "note", content });

            Assert.Contains(Synthetic.ElevatedClass, result.AssertedAuthority);
        }
    }

    /// <summary>The path policy can require elevation for the target path too, and a note creation
    /// routes through the write path that consults it.</summary>
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

    /// <summary>A basename no other note carries cannot change what any link resolves to, so the
    /// pinning pass does not run and <c>PinnedLinks</c> is null. A null member means the pass did
    /// not run; an empty list means it ran and pinned nothing.</summary>
    [Fact]
    public void AUniqueBasenameRunsNoPassAndReportsNoPinnedMember()
    {
        Assert.Null(Create(new { path = "Inbox/Unheard-Of.md", kind = "note", content = "# New\n" }).PinnedLinks);
    }

    /// <summary>A basename another note already carries runs the pinning pass, and the rewrites it
    /// applied are reported.</summary>
    [Fact]
    public void ADuplicatedBasenameRunsThePassAndReportsWhatItPinned()
    {
        var result = Create(new { path = "Projects/Widget/Notes.md", kind = "note", content = "# Notes\n" });

        var pinned = Assert.IsAssignableFrom<IReadOnlyList<VaultShared.Links.LinkRewrite>>(result.PinnedLinks);
        Assert.Equal(["Projects/Widget/Widget.md:3 Notes -> Reference/Notes"], pinned.Select(p => $"{p.Path}:{p.Line} {p.From} -> {p.To}"));
        Assert.Contains("[[Reference/Notes]]", _vault.TextOf("Projects/Widget/Widget.md"));
    }

    /// <summary>
    /// Pins are written before the note is. Do not reverse the order: if the run is interrupted
    /// after pinning, existing links still name the target they always named, but if it is
    /// interrupted after the note is written, bare links have silently changed meaning. A creation
    /// that fails to write its note must still have pinned.
    /// </summary>
    [Fact]
    public void PinsAreAppliedBeforeTheNoteIsWritten()
    {
        // The destination folder does not exist, so the note cannot be created; the pass runs
        // against a basename already in use and its rewrite must have landed anyway.
        var refusal = Refusal(new { path = "Nowhere/Notes.md", kind = "note", content = "# Notes\n" });

        Assert.Equal(ToolCodes.ParentMissing, refusal.Code);
        Assert.False(_vault.HasFile("Nowhere/Notes.md"));
        Assert.Contains("[[Reference/Notes]]", _vault.TextOf("Projects/Widget/Widget.md"));
    }

    /// <summary>Creating at a path that already exists pins nothing. The note is already among the
    /// candidates for its basename, so no link's resolution would change, and the refusal leaves
    /// the vault untouched.</summary>
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
        // A folder creates no note, so the pinning pass does not run and the member is null.
        Assert.Null(result.PinnedLinks);
    }

    /// <summary>
    /// A folder already at the target is refused, exactly as an occupied note path is. The tool's
    /// declaration promises an occupied target is refused, and it holds for both kinds.
    /// </summary>
    [Fact]
    public void AnOccupiedFolderTargetIsRefusedRatherThanReportedAsACreation()
    {
        var refusal = Refusal(new { path = "Projects/Widget", kind = "folder" });

        Assert.Equal(ToolCodes.AlreadyExists, refusal.Code);
        Assert.Contains("Projects/Widget", refusal.Message);
    }

    /// <summary>
    /// A folder creation refused for an occupied target seeds no placeholder, so a caller cannot
    /// drop a file into a folder someone else made by asking to create it again.
    /// </summary>
    [Fact]
    public void AnOccupiedFolderTargetSeedsNoPlaceholder()
    {
        Refusal(new { path = "Projects/Widget", kind = "folder", git_keep = true });

        Assert.False(_vault.HasFile("Projects/Widget/.gitkeep"));
    }

    /// <summary>The <c>.gitkeep</c> placeholder is derived from the requested folder rather than
    /// named by the caller, and it is created empty.</summary>
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
    /// A caller cannot name the placeholder from either direction. Asked for as a note it carries
    /// no markdown extension and is refused as an invalid path; asked for as a folder it reaches
    /// the access layer, whose operand rules exclude any dot segment regardless of the grant.
    /// </summary>
    [Fact]
    public void ACallerCannotNameThePlaceholderItself()
    {
        Assert.Equal(ToolCodes.InvalidPath, Refusal(new { path = "Projects/Held/.gitkeep", kind = "note", content = "" }).Code);
        Assert.Equal(ToolCodes.PathExcluded, Refusal(new { path = "Projects/Held/.gitkeep", kind = "folder" }).Code);
    }

    /// <summary>A folder creation consults the path policy directly. It performs no frontmatter
    /// write, so nothing else on the path would consult it.</summary>
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

    /// <summary>A name outside the grammar is refused, and the refusal suggests a conforming
    /// spelling where one can be derived.</summary>
    [Fact]
    public void ANameOutsideTheGrammarIsRefusedNamingASpellingThatWouldWork()
    {
        var refusal = Refusal(new { path = "Inbox/new thought.md", kind = "note", content = "# X\n" });

        Assert.Equal(ToolCodes.InvalidName, refusal.Code);
        Assert.Equal("New-thought.md", VaultToolHarness.Data(refusal)!["suggested"]);
        Assert.False(_vault.HasFile("Inbox/new thought.md"));
    }

    /// <summary>The markdown extension is never inferred. A note-kind call on an extensionless path
    /// is refused and told the spelling that would have worked.</summary>
    [Fact]
    public void ANoteKindCallOnAnExtensionlessPathIsRefusedWithTheSpelling()
    {
        var refusal = Refusal(new { path = "Inbox/Thought", kind = "note", content = "" });

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Inbox/Thought.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>The same rule in reverse: a folder path carries no extension, so a folder-kind call
    /// on a path ending in <c>.md</c> is a kind mismatch.</summary>
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
