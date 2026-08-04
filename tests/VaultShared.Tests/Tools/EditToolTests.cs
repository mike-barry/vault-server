using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the <c>edit</c> tool against a real directory tree and a synthetic frontmatter
/// declaration: the staleness guard, the substitution's exactly-once rule, what the result reports,
/// and how the write path's refusals reach a caller.
///
/// The frontmatter vocabulary is <see cref="Synthetic"/>'s, so no test here depends on the real
/// vault's registry.
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

    /// <summary>Builds the tool over a single vault's frontmatter declaration, the way a host holds
    /// it.</summary>
    /// <param name="state">The registry state to expose; a loaded synthetic registry by default.</param>
    /// <returns>An <see cref="EditTool"/> over that state.</returns>
    private static EditTool Tool(RegistryState? state = null) =>
        new(FrontmatterRegistries.Of("scratch", state ?? new RegistryState.Loaded(Synthetic.Registry)));

    private EditResult Edit(object arguments) => Edit(_vault, arguments);

    private static EditResult Edit(ScratchVault vault, object arguments, RegistryState? state = null) =>
        Assert.IsType<EditResult>(Assert.IsType<ToolResult.Data>(
            VaultToolHarness.Invoke(Tool(state), EditTool.Declaration, vault, arguments)).Value);

    private ToolRefusal Refusal(object arguments) => Refusal(_vault, arguments);

    /// <summary>
    /// Invokes the tool and returns the refusal a caller would see. The tool does not catch a
    /// <see cref="FrontmatterRefusal"/>; it is translated into a <see cref="ToolRefusal"/> here, the
    /// same step a front-end takes when it renders the envelope.
    /// </summary>
    /// <param name="vault">The scratch vault to run the call against.</param>
    /// <param name="arguments">The tool arguments, as an anonymous object.</param>
    /// <param name="state">The registry state to expose; a loaded synthetic registry by default.</param>
    /// <returns>The refusal the call produced.</returns>
    /// <exception cref="InvalidOperationException">The call succeeded instead of refusing.</exception>
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

    /// <summary>The declaration asks for a write floor. It is not the whole of the authority an
    /// edit needs: the write path asserts more depending on which keys the edit changed.</summary>
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

    /// <summary>Operations run in order, each seeing the result of the one before it, and only the
    /// ones that changed something are reported as applied.</summary>
    [Fact]
    public void OperationsApplyInOrderAndOnlyTheOnesThatChangedSomethingAreReported()
    {
        var result = Edit(new
        {
            path = "Projects/Widget/Widget.md",
            operations = new object[]
            {
                new { op = "set", key = "doc.title", value = "Gizmo" },
                // The key already holds this value, so this operation changes nothing.
                new { op = "set", key = "doc.type", value = "note" },
                new { op = "set", key = "doc.title", value = "Sprocket" },
            },
        });

        Assert.Equal([0, 2], result.AppliedOperations);
        Assert.Contains("doc.title: Sprocket", _vault.TextOf("Projects/Widget/Widget.md"), StringComparison.Ordinal);
    }

    /// <summary>A frontmatter operation changes the frontmatter only; the body survives
    /// verbatim.</summary>
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

    /// <summary>An anchor places the new entry beside the entry it names rather than appending
    /// it.</summary>
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

    /// <summary>Adding an entry that matches an existing one by its declared identity changes
    /// nothing. No bytes are written and no operation is reported as applied.</summary>
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

    /// <summary>A substitution must match exactly once. Several matches is a refusal carrying the
    /// count, never a bulk replacement.</summary>
    [Fact]
    public void ASubstitutionMatchingSeveralTimes_IsRefusedCarryingTheCount()
    {
        using var vault = new ScratchVault(opClasses: [Synthetic.OpenClass])
            .WithNote("Note.md", "# Note\n\nrepeat repeat repeat\n");

        var refusal = Refusal(vault, new { path = "Note.md", body = new { find = "repeat", replace = "once" } });

        Assert.Equal(ToolCodes.BodyMatchAmbiguous, refusal.Code);
        Assert.Equal("3", VaultToolHarness.Data(refusal)!["count"]);
    }

    /// <summary>A substitution searches the body only. Text inside the frontmatter block cannot be
    /// matched or rewritten through it.</summary>
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

    /// <summary>The hash guards the window the write lock cannot cover, between the caller's read
    /// and its write, during which an editor or a person may change the file. The refusal carries
    /// no data on purpose: returning the current hash would let a caller retry with it and
    /// overwrite content it never read, so the only recovery is to read the note again.</summary>
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

    /// <summary>The result carries the hash of what was written, and it equals the hash a read of
    /// the file returns, so a chain of edits needs no read between them.</summary>
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

    /// <summary>An edit that changes nothing returns the hash the file already had, so a chain of
    /// edits survives a no-op.</summary>
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

    /// <summary>The result reports the op classes the edit asserted, for the audit trail. Changing
    /// a key under a protected namespace contributes the class the declaration names for it.</summary>
    [Fact]
    public void TheResultEchoesTheAuthorityTheOutcomeAsserted()
    {
        var result = Edit(new { path = "Inbox/Loose.md", operations = new[] { new { op = "add_entry", key = "agent.instructions", entry = "One." } } });

        Assert.Equal([Synthetic.ElevatedClass], result.AssertedAuthority);
    }

    /// <summary>An edit that asserted no extra authority still reports an empty list, so a reader
    /// can tell it from a tool that never reports authority at all.</summary>
    [Fact]
    public void AnEditAssertingNothingStillCarriesTheEmptyReport()
    {
        var result = Edit(new { path = "Projects/Widget/Widget.md", operations = new[] { new { op = "set", key = "doc.title", value = "Gizmo" } } });

        Assert.Empty(result.AssertedAuthority);
    }

    /// <summary>A grant missing the op class the edit requires is refused, the refusal names the
    /// keys that required it, and nothing is written.</summary>
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

    /// <summary>Authority is classified from what changed, not from what the caller asked for. An
    /// operation that leaves a protected key exactly as it was asserts nothing.</summary>
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

    // ---- Behaviour when the frontmatter declaration did not load ----

    /// <summary>Without a declaration there is no way to tell whether a key is recognized, so a
    /// frontmatter write refuses. A declaration that is missing and one that failed to parse refuse
    /// with the same code and differ only in what they report.</summary>
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

    /// <summary>Only frontmatter writes are refused. A body-only edit reads no key and composes no
    /// block, so it runs; its authority comes from the path policy.</summary>
    [Fact]
    public void ABodyOnlyEditWithNoDeclaration_StillRuns()
    {
        Edit(
            _vault,
            new { path = "Projects/Widget/Widget.md", body = new { find = "Body text.", replace = "Other text." } },
            new RegistryState.Absent("declaration.json"));

        Assert.Contains("Other text.", _vault.TextOf("Projects/Widget/Widget.md"), StringComparison.Ordinal);
    }

    // ---- How the write path's refusals reach the caller ----

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

    /// <summary>A variant the declaration does not mark as clearable can only be removed one entry
    /// at a time, so a request to clear it as a whole is refused.</summary>
    [Fact]
    public void ClearingAVariantThatIsNotRemovableWholesale_IsRefused()
    {
        Assert.Equal(
            ToolCodes.InvalidArgument,
            Refusal(new { path = "Inbox/Loose.md", operations = new[] { new { op = "remove_entry", key = "agent.instructions", variant = "pinned" } } }).Code);
    }

    // ---- Existence and the limits of the grant ----

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

    /// <summary>A path is admitted against the grant's own roots and nothing wider. A note outside
    /// them is refused as out of scope rather than reported as missing, and the refusal carries no
    /// data.</summary>
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

    /// <summary>An always-readable root grants reads but not writes, so an edit to a note reachable
    /// only through one is refused as out of scope.</summary>
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
