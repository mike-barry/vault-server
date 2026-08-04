using System.Text.Json;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the <c>edit</c> tool's argument surface: what <c>Validate</c> refuses before a vault is
/// resolved or anything is locked, and what the decode step turns the wire arguments into.
///
/// Every check here is pure. The whole argument surface can be refused with no note, no frontmatter
/// declaration and no lock, so no test in this class needs a vault.
/// </summary>
public sealed class EditArgumentTests
{
    private static EditArguments Bind(object arguments) =>
        (EditArguments)NativeToolArguments.Bind(typeof(EditArguments), VaultToolHarness.Args(arguments));

    private static ArgumentFault Fault(object arguments) =>
        Bind(arguments).Validate() ?? throw new InvalidOperationException("The arguments held up, and the case expected a fault.");

    private static void Holds(object arguments) => Assert.Null(Bind(arguments).Validate());

    private static FrontmatterOperation Decoded(object operation)
    {
        var arguments = Bind(new { path = "Note.md", operations = new[] { operation } });
        Assert.Null(arguments.Validate());
        return Assert.Single(arguments.Ordered).Decode();
    }

    // ---- The operand and the overall call shape ----

    [Fact]
    public void AnAbsentPath_IsAFaultNamingIt()
    {
        Assert.Equal("path", Fault(new { operations = new[] { new { op = "remove", key = "doc.title" } } }).Argument);
    }

    /// <summary>An edit that names nothing to change is refused. It would take the write lock and
    /// change nothing.</summary>
    [Fact]
    public void AnEditCarryingNeitherAnOperationNorABodyChange_IsRefused()
    {
        Assert.Equal("operations", Fault(new { path = "Note.md" }).Argument);
    }

    // ---- The body change's two forms ----

    [Fact]
    public void AWholesaleReplacementAndASubstitutionTogether_AreRefused()
    {
        Assert.Equal("body", Fault(new { path = "Note.md", body = new { content = "new", find = "a", replace = "b" }, expected_content_hash = "abc" }).Argument);
    }

    [Fact]
    public void ABodyChangeNamingNeitherForm_IsRefused()
    {
        Assert.Equal("body", Fault(new { path = "Note.md", body = new { } }).Argument);
    }

    [Fact]
    public void AFindWithNoReplace_IsRefused()
    {
        Assert.Equal("body", Fault(new { path = "Note.md", body = new { find = "a" } }).Argument);
    }

    [Fact]
    public void AReplaceWithNoFind_IsRefused()
    {
        Assert.Equal("body", Fault(new { path = "Note.md", body = new { replace = "b" } }).Argument);
    }

    /// <summary>Blank search text is refused. It matches at every position, so the substitution's
    /// exactly-once requirement could never hold.</summary>
    [Fact]
    public void AnEmptyFind_IsRefused()
    {
        Assert.Equal("body", Fault(new { path = "Note.md", body = new { find = "   ", replace = "b" } }).Argument);
    }

    /// <summary>An empty replacement is accepted. It deletes the matched text, which is a change a
    /// caller can intend.</summary>
    [Fact]
    public void AnEmptyReplace_HoldsUp()
    {
        Holds(new { path = "Note.md", body = new { find = "a", replace = "" } });
    }

    // ---- The staleness guard, as an argument-shape rule ----

    [Fact]
    public void AWholesaleReplacementWithNoContentHash_IsRefusedNamingTheHash()
    {
        Assert.Equal("expected_content_hash", Fault(new { path = "Note.md", body = new { content = "new body" } }).Argument);
    }

    /// <summary>A content hash sent without a wholesale replacement is refused rather than ignored.
    /// A substitution already verifies itself through the text it matches, so the hash could not
    /// change the outcome.</summary>
    [Theory]
    [InlineData("""{"find":"a","replace":"b"}""")]
    [InlineData("null")]
    public void AContentHashWithNoWholesaleReplacement_IsRefused(string body)
    {
        var arguments = (EditArguments)NativeToolArguments.Bind(
            typeof(EditArguments),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                $$"""{"path":"Note.md","body":{{body}},"operations":[{"op":"remove","key":"doc.title"}],"expected_content_hash":"abc"}""")!);

        Assert.Equal("expected_content_hash", arguments.Validate()!.Argument);
    }

    [Fact]
    public void AWholesaleReplacementWithAContentHash_HoldsUp()
    {
        Holds(new { path = "Note.md", body = new { content = "new body" }, expected_content_hash = "abc" });
    }

    // ---- Per-operation coherence ----

    [Fact]
    public void AnUnrecognizedOperation_IsRefusedInThisVocabularyNamingItsPosition()
    {
        var fault = Fault(new { path = "Note.md", operations = new[] { new { op = "upsert", key = "doc.title" } } });

        Assert.Equal("operations[0]", fault.Argument);
        Assert.Contains("upsert", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>An operation name is trimmed and compared without regard to case.</summary>
    [Theory]
    [InlineData("  set  ")]
    [InlineData("SET")]
    [InlineData("Set")]
    public void AnOperationNameIsTrimmedAndCaseFolded(string op)
    {
        Holds(new { path = "Note.md", operations = new[] { new { op, key = "doc.title", value = "x" } } });
    }

    [Fact]
    public void AnOperationNamingNoKey_IsRefused()
    {
        Assert.Equal("operations[0]", Fault(new { path = "Note.md", operations = new[] { new { op = "remove" } } }).Argument);
    }

    /// <summary>The fault names the operation by its index in the array the caller sent.</summary>
    [Fact]
    public void AFaultNamesThePositionOfTheOperationItIsAbout()
    {
        var fault = Fault(new
        {
            path = "Note.md",
            operations = new object[]
            {
                new { op = "remove", key = "doc.title" },
                new { op = "remove", key = "doc.type" },
                new { op = "nonsense", key = "doc.owner" },
            },
        });

        Assert.Equal("operations[2]", fault.Argument);
    }

    [Theory]
    [InlineData("""{"op":"remove","key":"k","value":"x"}""")]
    [InlineData("""{"op":"set","key":"k","value":"x","entry":{"rule":"r"}}""")]
    [InlineData("""{"op":"set","key":"k","value":"x","target":"t"}""")]
    [InlineData("""{"op":"set","key":"k","value":"x","variant":"v"}""")]
    [InlineData("""{"op":"remove_entry","key":"k","target":"t","anchor":{"op":"after","target":"t"}}""")]
    public void AnArgumentThatCannotChangeTheOutcome_IsRefusedRatherThanIgnored(string operation)
    {
        var arguments = (EditArguments)NativeToolArguments.Bind(
            typeof(EditArguments),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($$"""{"path":"Note.md","operations":[{{operation}}]}""")!);

        Assert.Equal("operations[0]", arguments.Validate()!.Argument);
    }

    [Fact]
    public void ASetCarryingNoValue_IsRefused()
    {
        Assert.Equal("operations[0]", Fault(new { path = "Note.md", operations = new[] { new { op = "set", key = "doc.title" } } }).Argument);
    }

    /// <summary>An empty string is a valid scalar value here. Whether the key accepts it is decided
    /// by the declared constraint later, not by argument validation.</summary>
    [Fact]
    public void ASetOfEmptyText_HoldsUp()
    {
        Holds(new { path = "Note.md", operations = new[] { new { op = "set", key = "doc.title", value = "" } } });
    }

    [Fact]
    public void AnAddCarryingNoEntry_IsRefused()
    {
        Assert.Equal("operations[0]", Fault(new { path = "Note.md", operations = new[] { new { op = "add_entry", key = "doc.notes" } } }).Argument);
    }

    [Fact]
    public void AnEntryFieldThatIsNotAPlainValue_IsRefused()
    {
        Assert.Equal("operations[0]", Fault(new
        {
            path = "Note.md",
            operations = new[] { new { op = "add_entry", key = "doc.notes", entry = new { rule = new { nested = "deep" } } } },
        }).Argument);
    }

    /// <summary>An entry removal addresses either one entry by identity or one variant as a whole.
    /// Naming both contradicts itself.</summary>
    [Fact]
    public void ARemoveEntryNamingBothATargetAndAVariant_IsRefused()
    {
        Assert.Equal("operations[0]", Fault(new
        {
            path = "Note.md",
            operations = new[] { new { op = "remove_entry", key = "doc.notes", target = "t", variant = "v" } },
        }).Argument);
    }

    [Fact]
    public void ARemoveEntryNamingNeither_IsRefused()
    {
        Assert.Equal("operations[0]", Fault(new { path = "Note.md", operations = new[] { new { op = "remove_entry", key = "doc.notes" } } }).Argument);
    }

    // ---- Anchors ----

    [Fact]
    public void AnAnchorNamingNoPlacement_IsRefused()
    {
        var fault = Fault(new
        {
            path = "Note.md",
            operations = new[] { new { op = "add_entry", key = "doc.notes", entry = new { rule = "r" }, anchor = new { target = "t" } } },
        });

        Assert.Equal("operations[0].anchor", fault.Argument);
    }

    [Fact]
    public void AnAnchorNamingAnOperationRatherThanAPlacement_IsRefused()
    {
        var fault = Fault(new
        {
            path = "Note.md",
            operations = new[] { new { op = "add_entry", key = "doc.notes", entry = new { rule = "r" }, anchor = new { op = "add_entry", target = "t" } } },
        });

        Assert.Equal("operations[0].anchor", fault.Argument);
    }

    [Fact]
    public void AnAnchorNamingNoTarget_IsRefused()
    {
        Assert.Equal("operations[0].anchor", Fault(new
        {
            path = "Note.md",
            operations = new[] { new { op = "add_entry", key = "doc.notes", entry = new { rule = "r" }, anchor = new { op = "before" } } },
        }).Argument);
    }

    /// <summary>An anchor always addresses an entry under its operation's key, so carrying a key of
    /// its own could only contradict that.</summary>
    [Fact]
    public void AnAnchorCarryingAKey_IsRefused()
    {
        Assert.Equal("operations[0].anchor", Fault(new
        {
            path = "Note.md",
            operations = new[]
            {
                new { op = "add_entry", key = "doc.notes", entry = new { rule = "r" }, anchor = new { op = "after", key = "other", target = "t" } },
            },
        }).Argument);
    }

    // ---- Decoding wire arguments into operations ----

    [Fact]
    public void ASetDecodesToTheScalarCapability()
    {
        var operation = Assert.IsType<FrontmatterOperation.SetScalar>(Decoded(new { op = "set", key = " doc.title ", value = " Widget " }));

        // Trimming happens at construction, so every operation type sees the same text.
        Assert.Equal("doc.title", operation.Key);
        Assert.Equal("Widget", operation.Value);
    }

    [Fact]
    public void ARemoveDecodesToTheFieldCapability()
    {
        Assert.Equal("doc.title", Assert.IsType<FrontmatterOperation.RemoveField>(Decoded(new { op = "remove", key = "doc.title" })).Key);
    }

    /// <summary>An entry's fields keep the order the caller sent them in, which is the order the
    /// emitter writes them.</summary>
    [Fact]
    public void AnAddOfAMappingEntryDecodesItsFieldsInOrder()
    {
        var add = Assert.IsType<FrontmatterOperation.AddEntry>(
            Decoded(new { op = "add_entry", key = "agent.instructions", entry = new { use_skill = "vault-hygiene", when = "before a write" } }));

        var mapping = Assert.IsType<EntryPayload.Mapping>(add.Entry);
        Assert.Equal(["use_skill", "when"], mapping.Fields.Select(f => f.Name));
        Assert.Equal("vault-hygiene", mapping.Field("use_skill"));
        Assert.Null(add.Anchor);
    }

    /// <summary>A schema may declare a bare scalar as one of a key's entry variants, so the wire
    /// carries it as a plain value rather than as an object with a single field.</summary>
    [Fact]
    public void AnAddOfABareScalarEntryDecodesToTheScalarPayload()
    {
        var add = Assert.IsType<FrontmatterOperation.AddEntry>(
            Decoded(new { op = "add_entry", key = "agent.instructions", entry = "Keep the torque table current." }));

        Assert.Equal("Keep the torque table current.", Assert.IsType<EntryPayload.Scalar>(add.Entry).Value);
    }

    /// <summary>Every frontmatter scalar is held as text, so a JSON number arrives as the literal
    /// text it was written with.</summary>
    [Fact]
    public void ANonStringEntryValueDecodesToItsOwnLiteralText()
    {
        var add = (FrontmatterOperation.AddEntry)Decoded(new { op = "add_entry", key = "agent.instructions", entry = new { rule = "r", weight = 3 } });

        Assert.Equal("3", ((EntryPayload.Mapping)add.Entry).Field("weight"));
    }

    [Theory]
    [InlineData("before", AnchorPlacement.Before)]
    [InlineData("AFTER", AnchorPlacement.After)]
    public void AnAnchorDecodesToItsPlacementAndIdentity(string op, AnchorPlacement placement)
    {
        var add = (FrontmatterOperation.AddEntry)Decoded(new
        {
            op = "add_entry",
            key = "nav.contents",
            entry = new { link = "[[Widget]]", description = "d" },
            anchor = new { op, target = " [[Tasks]] " },
        });

        Assert.Equal(placement, add.Anchor!.Placement);
        Assert.Equal("[[Tasks]]", add.Anchor.Identity);
    }

    [Fact]
    public void ARemoveEntryByIdentityDecodesToTheIdentityTarget()
    {
        var remove = Assert.IsType<FrontmatterOperation.RemoveEntry>(Decoded(new { op = "remove_entry", key = "nav.contents", target = "[[Tasks]]" }));

        Assert.Equal("[[Tasks]]", Assert.IsType<EntryTarget.Identity>(remove.Target).Value);
    }

    [Fact]
    public void ARemoveEntryByVariantDecodesToTheVariantTarget()
    {
        var remove = (FrontmatterOperation.RemoveEntry)Decoded(new { op = "remove_entry", key = "agent.instructions", variant = "use_skill" });

        Assert.Equal("use_skill", Assert.IsType<EntryTarget.Variant>(remove.Target).Name);
    }
}
