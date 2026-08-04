using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the argument records of <c>create</c>, <c>move</c> and <c>delete</c>: what each one refuses
/// before a vault is resolved and before anything is locked.
///
/// The three are tested together because the rules they share are stated across the set: <c>kind</c>
/// is required exactly where the two kinds take different arguments, and an argument that cannot
/// affect the outcome for the given kind is refused rather than ignored.
/// </summary>
public sealed class MutationArgumentTests
{
    private static string? Fault(IToolArguments arguments) => arguments.Validate()?.Argument;

    // ---- The operand ----

    [Fact]
    public void EveryOneOfTheThreeRequiresItsPathOperand()
    {
        Assert.Equal("path", Fault(new CreateArguments("  ", OperandKind.Note, Content: "")));
        Assert.Equal("path", Fault(new DeleteArguments("", OperandKind.Folder)));
        Assert.Equal("path", Fault(new MoveArguments("", "Projects/Widget.md")));
    }

    /// <summary>A move needs a destination. It is spelled <c>destination</c> rather than <c>path</c>
    /// because it names something that does not exist yet.</summary>
    [Fact]
    public void MoveRequiresItsDestination()
    {
        Assert.Equal("destination", Fault(new MoveArguments("Inbox/Widget.md", "   ")));
    }

    // ---- Kind ----

    /// <summary><c>kind</c> is required on the two tools whose two kinds take different arguments.
    /// The property is nullable rather than defaulted, so omitting it is refused by name instead of
    /// silently meaning the first enum member.</summary>
    [Fact]
    public void KindIsRequiredOnCreateAndDelete()
    {
        Assert.Equal("kind", Fault(new CreateArguments("Inbox/Widget.md", null, Content: "")));
        Assert.Equal("kind", Fault(new DeleteArguments("Inbox/Widget.md", null)));
    }

    /// <summary>On <c>move</c> both kinds take the same arguments, so <c>kind</c> is optional. Sent,
    /// it only makes a mismatch fail sooner.</summary>
    [Fact]
    public void KindIsOptionalOnMove()
    {
        Assert.Null(Fault(new MoveArguments("Inbox/Widget.md", "Projects/Widget.md")));
        Assert.Null(Fault(new MoveArguments("Inbox/Widget.md", "Projects/Widget.md", Kind: OperandKind.Note)));
    }

    /// <summary>An unrecognized enum value is refused here, as an argument fault, rather than by the
    /// binder. <see cref="EnumArgument"/> is what lets it get this far.</summary>
    [Fact]
    public void AnUnrecognizedKindIsRefusedHereRatherThanByTheBinder()
    {
        var unrecognized = (OperandKind)(-1);

        Assert.Equal("kind", Fault(new CreateArguments("Inbox/Widget.md", unrecognized, Content: "")));
        Assert.Equal("kind", Fault(new DeleteArguments("Inbox/Widget.md", unrecognized)));
        Assert.Equal("kind", Fault(new MoveArguments("Inbox/Widget.md", "Projects/Widget.md", Kind: unrecognized)));
    }

    /// <summary>The same value sent on the wire binds without throwing, so the refusal a caller
    /// meets is the argument fault.</summary>
    [Fact]
    public void AnUnrecognizedKindSurvivesBindingSoTheToolCanRefuseIt()
    {
        var bound = NativeToolArguments.Bind(
            typeof(CreateArguments), VaultToolHarness.Args(new { path = "Inbox/Widget.md", kind = "note-ish", content = "" }));

        Assert.Equal("kind", Fault(bound));
    }

    /// <summary>An omitted optional <c>kind</c> binds to null rather than to an unrecognized value,
    /// because the converter that tolerates unknown names is wrapped by the nullable handling rather
    /// than replacing it.</summary>
    [Fact]
    public void AnAbsentOptionalKindBindsToNothingAtAll()
    {
        var bound = (MoveArguments)NativeToolArguments.Bind(
            typeof(MoveArguments), VaultToolHarness.Args(new { path = "Inbox/Widget.md", destination = "Projects/Widget.md" }));

        Assert.Null(bound.Kind);
        Assert.Null(Fault(bound));
    }

    // ---- Arguments that cannot affect the outcome ----

    [Fact]
    public void GitKeepOnANoteIsRefusedRatherThanIgnored()
    {
        Assert.Equal("git_keep", Fault(new CreateArguments("Inbox/Widget.md", OperandKind.Note, Content: "", GitKeep: true)));
    }

    [Fact]
    public void ContentOnAFolderIsRefusedRatherThanIgnored()
    {
        Assert.Equal("content", Fault(new CreateArguments("Projects/Widget", OperandKind.Folder, Content: "# Widget")));
    }

    [Fact]
    public void RecursiveOnANoteIsRefusedRatherThanIgnored()
    {
        Assert.Equal("recursive", Fault(new DeleteArguments("Inbox/Widget.md", OperandKind.Note, Recursive: true)));
    }

    /// <summary>A note creation requires <c>content</c>, since nothing is templated or composed for
    /// the caller. Empty text is accepted and creates an empty note.</summary>
    [Fact]
    public void ANoteCreationRequiresItsContentAndTakesEmptyTextForAnEmptyNote()
    {
        Assert.Equal("content", Fault(new CreateArguments("Inbox/Widget.md", OperandKind.Note)));
        Assert.Null(Fault(new CreateArguments("Inbox/Widget.md", OperandKind.Note, Content: "")));
    }

    /// <summary>An argument is accepted on the kind it applies to.</summary>
    [Fact]
    public void EachKindTakesItsOwnArguments()
    {
        Assert.Null(Fault(new CreateArguments("Projects/Widget", OperandKind.Folder, GitKeep: true)));
        Assert.Null(Fault(new DeleteArguments("Projects/Widget", OperandKind.Folder, Recursive: true)));
    }

    /// <summary>Both tools that preview spell the gate <c>apply</c>, and it is false by
    /// default.</summary>
    [Fact]
    public void ThePreviewGateIsOffByDefaultOnBothToolsThatCarryOne()
    {
        Assert.False(new DeleteArguments("Inbox/Widget.md", OperandKind.Note).Apply);
        Assert.False(new MoveArguments("Inbox/Widget.md", "Projects/Widget.md").Apply);
    }
}
