using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// Pinning on creation: a basename already in use triggers the pass and nothing else does, every
/// bare link the new note would re-aim is frozen on what it opens now, and no link is ever aimed at
/// the new note.
/// </summary>
public sealed class WikilinkPinningTests : IDisposable
{
    private readonly ScratchVault _vault = new();

    public void Dispose() => _vault.Dispose();

    private LinkPassOutcome<WikilinkPlan> Creating(string path) =>
        WikilinkMaintenance.PlanCreation(_vault.Access.BypassingScope(), _vault.At(path));

    // ---- What triggers the pass ----

    [Fact]
    public void AUniqueBasenameRunsNoPassAtAll()
    {
        // Structurally distinct from a pass that ran and pinned nothing, which is the distinction a
        // creation result's nullable pinned-links member carries.
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n");

        Assert.IsType<LinkPassOutcome<WikilinkPlan>.NotNeeded>(Creating("Aa/Widget.md"));
    }

    [Fact]
    public void ADuplicatedBasenameThatChangesNothingIsAPassThatPinnedNothing()
    {
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Zz/Note.md", "See [[Dupe]].\n");

        // The new note is further from the linking note than the one it already opens.
        var plan = LinkPass.Plan(Creating("Aa/Bb/Cc/Dupe.md"));

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Relocated);
    }

    // ---- What is pinned ----

    [Fact]
    public void ABareLinkTheNewNoteWouldReAimIsPinnedToWhatItOpensNow()
    {
        _vault
            .WithNote("Zz/Dupe.md", "# The one it opens now\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n")
            .WithNote("Zz/Other.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Aa/Bb/Dupe.md"));

        // Only the link whose answer would change: `Zz/Other.md` keeps opening its own neighbour.
        Assert.Equal(["Aa/Note.md:1 Dupe -> Zz/Dupe"], LinkPass.Apply(_vault, plan));
        Assert.Equal("See [[Dupe]].\n", _vault.TextOf("Zz/Other.md"));
    }

    [Fact]
    public void APinIsAlwaysTheQualifiedForm()
    {
        // Protection names one file unambiguously, so it never emits the short form even where the
        // name would still be unique somewhere.
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Aa/Bb/Dupe.md"));

        Assert.Equal("Zz/Dupe", plan.Rewrites.Single().To);
    }

    [Fact]
    public void NoLinkIsEverAimedAtTheNewNote()
    {
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Aa/Bb/Dupe.md"));

        Assert.DoesNotContain(plan.Rewrites, r => r.To.Contains("Aa/Bb", StringComparison.Ordinal));
    }

    [Fact]
    public void APathQualifiedLinkIsLeftAloneBecauseItsMeaningCannotChange()
    {
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Aa/Note.md", "See [[Zz/Dupe]].\n");

        Assert.True(LinkPass.Plan(Creating("Aa/Bb/Dupe.md")).IsEmpty);
    }

    // ---- The order ----

    [Fact]
    public void PinningIsComputedAndAppliedWithTheNoteStillUnwritten()
    {
        // Interrupted after pinning, the vault holds links naming a target that is still correct.
        // The area only reads and rewrites; bringing the note into existence is the tool's own
        // operation and is scope-checked normally.
        _vault
            .WithNote("Zz/Dupe.md", "# Existing\n")
            .WithNote("Aa/Note.md", "See [[Dupe]].\n");

        var plan = LinkPass.Plan(Creating("Aa/Bb/Dupe.md"));
        LinkPass.Apply(_vault, plan);

        Assert.False(_vault.HasFile("Aa/Bb/Dupe.md"));
        Assert.Equal("See [[Zz/Dupe]].\n", _vault.TextOf("Aa/Note.md"));
    }
}
