using System.Text.Json;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// <c>edit</c>'s argument surface: what <c>Validate</c> refuses before a vault is resolved or
/// anything is locked, and what the one decode site turns the wire projection into.
///
/// Everything here is pure. That is the point of the phase: the whole argument surface is refusable
/// with no note, no declaration and no lock, which is what makes the failure ordering structural
/// rather than a claim about where the checks happen to sit.
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

    // ---- The operand and the call's own shape ----

    [Fact]
    public void AnAbsentPath_IsAFaultNamingIt()
    {
        Assert.Equal("path", Fault(new { operations = new[] { new { op = "remove", key = "doc.title" } } }).Argument);
    }

    /// <summary>An edit naming nothing to change is a read spelled as a mutation: it takes the
    /// lock, asserts nothing, and answers a hash.</summary>
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

    /// <summary>Empty text anchors nothing - it occurs everywhere, so "exactly once" could never
    /// hold.</summary>
    [Fact]
    public void AnEmptyFind_IsRefused()
    {
        Assert.Equal("body", Fault(new { path = "Note.md", body = new { find = "   ", replace = "b" } }).Argument);
    }

    /// <summary>An empty replacement deletes what was matched, which is a change a caller means.</summary>
    [Fact]
    public void AnEmptyReplace_HoldsUp()
    {
        Holds(new { path = "Note.md", body = new { find = "a", replace = "" } });
    }

    // ---- The staleness guard, as a shape rule ----

    [Fact]
    public void AWholesaleReplacementWithNoContentHash_IsRefusedNamingTheHash()
    {
        Assert.Equal("expected_content_hash", Fault(new { path = "Note.md", body = new { content = "new body" } }).Argument);
    }

    /// <summary>Inert rather than ignored: a substitution self-verifies through its own anchor, so
    /// a hash beside one cannot change an outcome.</summary>
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

    /// <summary>Trimmed and case-folded before comparison, as every operation name is.</summary>
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

    /// <summary>The position is the caller's own index, so a fault in a long batch names the
    /// operation it can find.</summary>
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

    /// <summary>An empty string is a legitimate scalar value - what bounds it is the declared
    /// constraint, not this phase.</summary>
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

    /// <summary>A removal addresses one entry by identity or one variant wholesale. Both is a
    /// contradiction, and neither addresses nothing.</summary>
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

    /// <summary>An anchor addresses an entry under its operation's own key, so a key on it could
    /// only disagree with that one.</summary>
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

    // ---- The one decode site ----

    [Fact]
    public void ASetDecodesToTheScalarCapability()
    {
        var operation = Assert.IsType<FrontmatterOperation.SetScalar>(Decoded(new { op = "set", key = " doc.title ", value = " Widget " }));

        // Trimming happens where an operation is constructed, so every capability sees the same text.
        Assert.Equal("doc.title", operation.Key);
        Assert.Equal("Widget", operation.Value);
    }

    [Fact]
    public void ARemoveDecodesToTheFieldCapability()
    {
        Assert.Equal("doc.title", Assert.IsType<FrontmatterOperation.RemoveField>(Decoded(new { op = "remove", key = "doc.title" })).Key);
    }

    /// <summary>Fields keep the order they were sent in, because that is the order the emitter
    /// writes and a caller composing an entry chose it.</summary>
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

    /// <summary>A bare value is a variant a schema may declare, so the wire carries one as a plain
    /// value rather than as an object with a conventional field inside it.</summary>
    [Fact]
    public void AnAddOfABareScalarEntryDecodesToTheScalarPayload()
    {
        var add = Assert.IsType<FrontmatterOperation.AddEntry>(
            Decoded(new { op = "add_entry", key = "agent.instructions", entry = "Keep the torque table current." }));

        Assert.Equal("Keep the torque table current.", Assert.IsType<EntryPayload.Scalar>(add.Entry).Value);
    }

    /// <summary>The parse leaves every frontmatter scalar as text, so a value that is a number in a
    /// client's eyes is that literal's own text in the note.</summary>
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
