using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// <c>edit</c> over a real tree and a synthetic declaration: the staleness guard, the substitution's
/// exactly-once rule, what the result reports, and how the write path's refusals reach a caller.
///
/// The vocabulary is <see cref="Synthetic"/>'s, so nothing here asserts anything about the vault's
/// real one. What is under test is the tool.
/// </summary>
public sealed class EditToolTests : IDisposable
{
    private const string Fenced = "---\ndoc.title: Widget\ndoc.type: note\n---\n# Widget\n\nBody text.\n";
    private const string Unfenced = "# Loose\n\nNo block at all.\n";

    private readonly ScratchVault _vault = Scratch(Synthetic.OpenClass, Synthetic.ElevatedClass);

    public void Dispose() => _vault.Dispose();

    private static ScratchVault Scratch(params string[] opClasses) =>
        new ScratchVault(opClasses: opClasses)
            .WithNote("Projects/Widget/Widget.md", Fenced)
            .WithNote("Inbox/Loose.md", Unfenced);

    /// <summary>The tool over one vault's declaration, which is what a host holds and what the
    /// fail-closed seam reads.</summary>
    private static EditTool Tool(RegistryState? state = null) =>
        new(FrontmatterRegistries.Of("scratch", state ?? new RegistryState.Loaded(Synthetic.Registry)));

    private EditResult Edit(object arguments) => Edit(_vault, arguments);

    private static EditResult Edit(ScratchVault vault, object arguments, RegistryState? state = null) =>
        Assert.IsType<EditResult>(Assert.IsType<ToolResult.Data>(
            VaultToolHarness.Invoke(Tool(state), EditTool.Declaration, vault, arguments)).Value);

    private ToolRefusal Refusal(object arguments) => Refusal(_vault, arguments);

    /// <summary>
    /// The refusal as a caller sees it. <b>The tool catches nothing it did not raise</b>: a
    /// <see cref="FrontmatterRefusal"/> travels out in its own vocabulary and is named through the
    /// one table, where the envelope is rendered. This is that step, which is what a front-end does
    /// and what a case about the wire code has to go through.
    /// </summary>
    private static ToolRefusal Refusal(ScratchVault vault, object arguments, RegistryState? state = null)
    {
        try
        {
            VaultToolHarness.Invoke(Tool(state), EditTool.Declaration, vault, arguments);
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

    private static string HashOf(ScratchVault vault, string path) =>
        Assert.IsType<ReadOutcome.Content>(VaultFiles.Read(vault.ToRead(path))).ContentHash;

    // ---- The declaration ----

    /// <summary>A write floor, and it is not the whole authority: what a particular edit requires
    /// depends on which keys it turned out to change, asserted inside the write path.</summary>
    [Fact]
    public void DeclaresAWriteFloorAndMutates()
    {
        Assert.Equal(new Floor.Class("content:write"), EditTool.Declaration.OpClassFloor);
        Assert.True(EditTool.Declaration.Mutates);
        Assert.False(EditTool.Declaration.RequiresClientSession);
    }

    // ---- Frontmatter operations ----

    [Fact]
    public void ASetWritesTheValueAndReportsTheKeyAsChanged()
    {
        var result = Edit(new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "set", key = "doc.title", value = "Gizmo" } } });

        Assert.Contains("doc.title: Gizmo", _vault.TextOf("Projects/Widget/Widget.md"), StringComparison.Ordinal);
        Assert.True(result.FrontmatterChanged);
        Assert.False(result.BodyChanged);
        Assert.Equal(["doc.title"], result.ChangedKeys);
        Assert.Equal([0], result.AppliedOperations);
    }

    /// <summary>Operations are ordered and each sees the previous result, and only the ones that
    /// changed something are reported applied.</summary>
    [Fact]
    public void OperationsApplyInOrderAndOnlyTheOnesThatChangedSomethingAreReported()
    {
        var result = Edit(new
        {
            path = "Projects/Widget/Widget.md",
            operations = new object[]
            {
                new { op = "set", key = "doc.title", value = "Gizmo" },
                // Already what it says, so a pure no-op that changes nothing.
                new { op = "set", key = "doc.type", value = "note" },
                new { op = "set", key = "doc.title", value = "Sprocket" },
            },
        });

        Assert.Equal([0, 2], result.AppliedOperations);
        Assert.Contains("doc.title: Sprocket", _vault.TextOf("Projects/Widget/Widget.md"), StringComparison.Ordinal);
    }

    /// <summary>The body survives verbatim: a frontmatter mutation changes the tree and nothing
    /// else.</summary>
    [Fact]
    public void AFrontmatterOperationLeavesTheBodyAlone()
    {
        Edit(new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "set", key = "doc.title", value = "Gizmo" } } });

        Assert.EndsWith("# Widget\n\nBody text.\n", _vault.TextOf("Projects/Widget/Widget.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddCreatesTheKeyAndTheBlockOnANoteWithNone()
    {
        var result = Edit(new
        {
            path = "Inbox/Loose.md",
            operations = new[] { new { op = "add_entry", key = "agent.instructions", entry = "Keep it short." } },
        });

        var text = _vault.TextOf("Inbox/Loose.md");
        Assert.StartsWith("---\nagent.instructions:\n  - Keep it short.\n---\n", text, StringComparison.Ordinal);
        Assert.EndsWith(Unfenced, text, StringComparison.Ordinal);
        Assert.True(result.FrontmatterChanged);
    }

    /// <summary>An anchor places the entry beside the one it names, rather than appending.</summary>
    [Fact]
    public void AnAnchoredAddLandsBesideTheEntryItNames()
    {
        Edit(new
        {
            path = "Inbox/Loose.md",
            operations = new object[]
            {
                new { op = "add_entry", key = "agent.instructions", entry = new { routed = "first" } },
                new { op = "add_entry", key = "agent.instructions", entry = new { routed = "second" } },
                new
                {
                    op = "add_entry",
                    key = "agent.instructions",
                    entry = new { routed = "wedged" },
                    anchor = new { op = "before", target = "second" },
                },
            },
        });

        var text = _vault.TextOf("Inbox/Loose.md");
        Assert.True(
            text.IndexOf("wedged", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal),
            text);
    }

    /// <summary>A duplicate add is a pure no-op keyed on declared identity: nothing is written and
    /// no operation is reported applied.</summary>
    [Fact]
    public void ADuplicateAddChangesNothingAndLeavesTheFileByteIdentical()
    {
        Edit(new { path = "Inbox/Loose.md", operations = new[] { new { op = "add_entry", key = "agent.instructions", entry = new { routed = "here" } } } });
        var before = _vault.BytesOf("Inbox/Loose.md");

        var result = Edit(new { path = "Inbox/Loose.md", operations = new[] { new { op = "add_entry", key = "agent.instructions", entry = new { routed = "HERE" } } } });

        Assert.Empty(result.AppliedOperations);
        Assert.False(result.FrontmatterChanged);
        Assert.Equal(before, _vault.BytesOf("Inbox/Loose.md"));
    }

    // ---- The body's two forms ----

    [Fact]
    public void AWholesaleReplacementRewritesTheBodyAndLeavesTheBlockAlone()
    {
        var result = Edit(new
        {
            path = "Projects/Widget/Widget.md",
            body = new { content = "# Rewritten\n" },
            expected_content_hash = HashOf(_vault, "Projects/Widget/Widget.md"),
        });

        Assert.Equal("---\ndoc.title: Widget\ndoc.type: note\n---\n# Rewritten\n", _vault.TextOf("Projects/Widget/Widget.md"));
        Assert.True(result.BodyChanged);
        Assert.False(result.FrontmatterChanged);
        Assert.Empty(result.ChangedKeys);
    }

    [Fact]
    public void ASubstitutionMatchingExactlyOnceReplacesIt()
    {
        var result = Edit(new { path = "Projects/Widget/Widget.md", body = new { find = "Body text.", replace = "Different text." } });

        Assert.Contains("Different text.", _vault.TextOf("Projects/Widget/Widget.md"), StringComparison.Ordinal);
        Assert.True(result.BodyChanged);
    }

    [Fact]
    public void ASubstitutionMatchingNothing_IsRefusedAndWritesNothing()
    {
        var before = _vault.BytesOf("Projects/Widget/Widget.md");

        var refusal = Refusal(new { path = "Projects/Widget/Widget.md", body = new { find = "absent text", replace = "x" } });

        Assert.Equal(ToolCodes.BodyMatchNotFound, refusal.Code);
        Assert.Equal(before, _vault.BytesOf("Projects/Widget/Widget.md"));
    }

    /// <summary>Exactly-once matching is the self-verification of a surgical edit, so several
    /// matches is a refusal carrying the count rather than a bulk replacement.</summary>
    [Fact]
    public void ASubstitutionMatchingSeveralTimes_IsRefusedCarryingTheCount()
    {
        using var vault = new ScratchVault(opClasses: [Synthetic.OpenClass])
            .WithNote("Note.md", "# Note\n\nrepeat repeat repeat\n");

        var refusal = Refusal(vault, new { path = "Note.md", body = new { find = "repeat", replace = "once" } });

        Assert.Equal(ToolCodes.BodyMatchAmbiguous, refusal.Code);
        Assert.Equal("3", VaultToolHarness.Data(refusal)!["count"]);
    }

    /// <summary>A substitution's anchor matches the body facet only: the note's frontmatter is not
    /// reachable through it, which is what keeps a body patch body-confined.</summary>
    [Fact]
    public void ASubstitutionCannotReachIntoTheFrontmatter()
    {
        Assert.Equal(
            ToolCodes.BodyMatchNotFound,
            Refusal(new { path = "Projects/Widget/Widget.md", body = new { find = "doc.title: Widget", replace = "doc.title: Hijacked" } }).Code);
    }

    // ---- The staleness guard ----

    [Fact]
    public void AWholesaleReplacementAgainstTheCurrentHash_IsAdmitted()
    {
        Edit(new
        {
            path = "Inbox/Loose.md",
            body = new { content = "# New\n" },
            expected_content_hash = HashOf(_vault, "Inbox/Loose.md"),
        });

        Assert.Equal("# New\n", _vault.TextOf("Inbox/Loose.md"));
    }

    /// <summary>The window the lock cannot cover: model time, during which the editor or a person
    /// changes the file. The refusal carries nothing - returning the current hash would let a caller
    /// retry with it and clobber content it never read, so recovery is a re-read.</summary>
    [Fact]
    public void AWholesaleReplacementAgainstAStaleHash_IsRefusedWithNoPayload()
    {
        var stale = HashOf(_vault, "Inbox/Loose.md");
        _vault.WithNote("Inbox/Loose.md", "# Somebody else got here first\n");

        var refusal = Refusal(new { path = "Inbox/Loose.md", body = new { content = "# Mine\n" }, expected_content_hash = stale });

        Assert.Equal(ToolCodes.ContentChanged, refusal.Code);
        Assert.Null(VaultToolHarness.Data(refusal));
        Assert.Equal("# Somebody else got here first\n", _vault.TextOf("Inbox/Loose.md"));
    }

    // ---- Chaining ----

    /// <summary>The hash comes back from the write, so a chain of edits needs no interleaved read -
    /// and it is the same value a read of the file answers.</summary>
    [Fact]
    public void TheReturnedHashIsTheOneAReadOfTheFileAnswers()
    {
        var result = Edit(new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "set", key = "doc.title", value = "Gizmo" } } });

        Assert.Equal(HashOf(_vault, "Projects/Widget/Widget.md"), result.ContentHash);
    }

    [Fact]
    public void TheReturnedHashChainsStraightIntoTheNextWholesaleReplacement()
    {
        var first = Edit(new { path = "Inbox/Loose.md", operations = new[] { new { op = "add_entry", key = "agent.instructions", entry = "One." } } });

        var second = Edit(new { path = "Inbox/Loose.md", body = new { content = "# Chained\n" }, expected_content_hash = first.ContentHash });

        Assert.EndsWith("# Chained\n", _vault.TextOf("Inbox/Loose.md"), StringComparison.Ordinal);
        Assert.Equal(HashOf(_vault, "Inbox/Loose.md"), second.ContentHash);
    }

    /// <summary>An edit that turns out to change nothing answers the hash the file already had, so
    /// a chain survives a no-op.</summary>
    [Fact]
    public void ANoOpAnswersTheHashTheFileAlreadyHad()
    {
        var before = HashOf(_vault, "Projects/Widget/Widget.md");

        var result = Edit(new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "remove", key = "doc.owner" } } });

        Assert.Equal(before, result.ContentHash);
        Assert.Empty(result.AppliedOperations);
        Assert.False(result.FrontmatterChanged);
        Assert.False(result.BodyChanged);
    }

    // ---- Authority ----

    /// <summary>What the outcome required, echoed for the audit trail. A key under a protected
    /// namespace contributes the class the declaration names.</summary>
    [Fact]
    public void TheResultEchoesTheAuthorityTheOutcomeAsserted()
    {
        var result = Edit(new { path = "Inbox/Loose.md", operations = new[] { new { op = "add_entry", key = "agent.instructions", entry = "One." } } });

        Assert.Equal([Synthetic.ElevatedClass], result.AssertedAuthority);
    }

    /// <summary>Empty stays: absence would not distinguish an operation that asserted nothing from
    /// a tool that never asserts.</summary>
    [Fact]
    public void AnEditAssertingNothingStillCarriesTheEmptyReport()
    {
        var result = Edit(new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "set", key = "doc.title", value = "Gizmo" } } });

        Assert.Empty(result.AssertedAuthority);
    }

    /// <summary>A grant lacking the class the outcome requires is denied, the denial names the
    /// changed key set - the call's own coordinates - and nothing is written.</summary>
    [Fact]
    public void AGrantLackingTheClassTheOutcomeRequires_IsDeniedAndWritesNothing()
    {
        using var vault = Scratch(Synthetic.OpenClass);
        var before = vault.BytesOf("Inbox/Loose.md");

        var refusal = Refusal(vault, new { path = "Inbox/Loose.md", operations = new[] { new { op = "set", key = "agent.mode", value = "assist" } } });

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.Contains("agent.mode", VaultToolHarness.Data(refusal)!["keys"], StringComparison.Ordinal);
        Assert.Equal(before, vault.BytesOf("Inbox/Loose.md"));
    }

    /// <summary>Classification is over the outcome rather than over what the caller said it was
    /// doing: an operation that leaves a protected key exactly as it was requires nothing.</summary>
    [Fact]
    public void AnOperationThatChangesAProtectedKeyToWhatItAlreadySays_AssertsNothing()
    {
        using var vault = new ScratchVault(opClasses: [Synthetic.OpenClass])
            .WithNote("Note.md", "---\nagent.mode: assist\ndoc.title: Note\n---\n# Note\n");

        var result = Edit(vault, new
        {
            path = "Note.md",
            operations = new object[]
            {
                new { op = "set", key = "agent.mode", value = "assist" },
                new { op = "set", key = "doc.title", value = "Renamed" },
            },
        });

        Assert.Empty(result.AssertedAuthority);
        Assert.Equal(["doc.title"], result.ChangedKeys);
    }

    // ---- The fail-closed seam ----

    /// <summary>No declaration means no way to tell whether a key is recognized, so a frontmatter
    /// write refuses. Absent and unloadable refuse identically and report differently.</summary>
    [Theory]
    [InlineData("absent")]
    [InlineData("unloadable")]
    public void AnEditInterpretingFrontmatterWithNoDeclaration_RefusesFailClosed(string state)
    {
        var refusal = Refusal(
            _vault,
            new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "set", key = "doc.title", value = "Gizmo" } } },
            state == "absent"
                ? new RegistryState.Absent("declaration.json")
                : new RegistryState.Unloadable("declaration.json", "it names an undeclared op class."));

        Assert.Equal(ToolCodes.FrontmatterUnavailable, refusal.Code);
        Assert.Equal(Fenced, _vault.TextOf("Projects/Widget/Widget.md"));
    }

    /// <summary>What a failed load refuses is exactly the frontmatter writes. A body-only edit
    /// reads no key and composes no block, so it is untouched - what prices it is path policy.</summary>
    [Fact]
    public void ABodyOnlyEditWithNoDeclaration_StillRuns()
    {
        Edit(
            _vault,
            new { path = "Projects/Widget/Widget.md", body = new { find = "Body text.", replace = "Other text." } },
            new RegistryState.Absent("declaration.json"));

        Assert.Contains("Other text.", _vault.TextOf("Projects/Widget/Widget.md"), StringComparison.Ordinal);
    }

    // ---- The write path's refusals, in the wire vocabulary ----

    [Fact]
    public void AnUndeclaredKey_ReachesTheCallerAsUnknownKey()
    {
        var refusal = Refusal(new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "set", key = "made.up", value = "x" } } });

        Assert.Equal(ToolCodes.UnknownKey, refusal.Code);
        Assert.Contains("made.up", VaultToolHarness.Data(refusal)!["keys"], StringComparison.Ordinal);
    }

    [Fact]
    public void AScalarOperationAgainstABlockListKey_ReachesTheCallerAsShapeMismatch()
    {
        Assert.Equal(
            ToolCodes.ShapeMismatch,
            Refusal(new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "set", key = "nav.contents", value = "x" } } }).Code);
    }

    [Fact]
    public void AValueTheDeclarationRefuses_ReachesTheCallerAsInvalidValue()
    {
        Assert.Equal(
            ToolCodes.InvalidValue,
            Refusal(new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "set", key = "doc.created", value = "last Tuesday" } } }).Code);
    }

    [Fact]
    public void AnAnchorNamingNoEntry_ReachesTheCallerAsEntryNotFound()
    {
        Assert.Equal(
            ToolCodes.EntryNotFound,
            Refusal(new
            {
                path = "Inbox/Loose.md",
                operations = new[]
                {
                    new { op = "add_entry", key = "agent.instructions", entry = new { routed = "one" }, anchor = new { op = "after", target = "nothing" } },
                },
            }).Code);
    }

    /// <summary>A variant a human authored is dropped one at a time, so clearing it wholesale is
    /// refused - and the remedy is a differently shaped request.</summary>
    [Fact]
    public void ClearingAVariantThatIsNotRemovableWholesale_IsRefused()
    {
        Assert.Equal(
            ToolCodes.InvalidArgument,
            Refusal(new { path = "Inbox/Loose.md", operations = new[] { new { op = "remove_entry", key = "agent.instructions", variant = "pinned" } } }).Code);
    }

    // ---- Existence, and the operand perimeter ----

    [Fact]
    public void ANoteThatIsNotThere_IsAMissRatherThanACreation()
    {
        var refusal = Refusal(new { path = "Inbox/Absent.md", operations = new[] { new { op = "set", key = "doc.title", value = "x" } } });

        Assert.Equal(ToolCodes.PathNotFound, refusal.Code);
        Assert.False(_vault.HasFile("Inbox/Absent.md"));
    }

    [Fact]
    public void AnExtensionlessOperand_RefusesNamingTheSpellingThatWouldHaveWorked()
    {
        var refusal = Refusal(new { path = "Inbox/Loose", operations = new[] { new { op = "set", key = "doc.title", value = "x" } } });

        Assert.Equal(ToolCodes.InvalidPath, refusal.Code);
        Assert.Equal("Inbox/Loose.md", VaultToolHarness.Data(refusal)!["suggested"]);
    }

    /// <summary>The operand is admitted against the grant's own roots and nothing wider, so a note
    /// outside them refuses loudly rather than reading as a miss.</summary>
    [Fact]
    public void ANamedPathOutsideTheGrant_RefusesLoudly()
    {
        using var scoped = new ScratchVault(VaultScope.Of(["Projects"]), opClasses: [Synthetic.OpenClass])
            .WithNote("Projects/Widget/Widget.md", Fenced)
            .WithNote("Inbox/Loose.md", Unfenced);

        var refusal = Refusal(scoped, new { path = "Inbox/Loose.md", operations = new[] { new { op = "set", key = "doc.title", value = "x" } } });

        Assert.Equal(ToolCodes.OutOfScope, refusal.Code);
        Assert.Null(VaultToolHarness.Data(refusal));
    }

    /// <summary>An always-readable root admits a read and refuses a write, which is the asymmetry
    /// the two path types carry.</summary>
    [Fact]
    public void ANoteReachableOnlyThroughAnAlwaysReadableRoot_RefusesTheWrite()
    {
        using var vault = new ScratchVault(
            VaultScope.Of(["Projects"]),
            opClasses: [Synthetic.OpenClass],
            policy: Synthetic.Policy("""[{ "path": "Guides", "always_readable": true }]"""))
            .WithNote("Guides/Vault-Guide.md", Fenced);

        Assert.Equal(
            ToolCodes.OutOfScope,
            Refusal(vault, new { path = "Guides/Vault-Guide.md", operations = new[] { new { op = "set", key = "doc.title", value = "x" } } }).Code);
    }
}
