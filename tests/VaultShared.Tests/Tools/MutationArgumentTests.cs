using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// The pure argument surfaces of <c>create</c>, <c>move</c> and <c>delete</c>: what each record can
/// refuse before a vault is resolved and before anything is locked.
///
/// The three are here together because the rules they turn on are one rule each, stated across the
/// set - kind is required exactly where the two kinds take different arguments, and an argument that
/// cannot change an outcome for the supplied operand is refused rather than ignored - and a copy per
/// tool would let the copies drift.
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

    /// <summary>The one constructed operand in the set, and the reason its spelling differs from
    /// <c>path</c>: it names something that does not exist yet.</summary>
    [Fact]
    public void MoveRequiresItsDestination()
    {
        Assert.Equal("destination", Fault(new MoveArguments("Inbox/Widget.md", "   ")));
    }

    // ---- Kind ----

    /// <summary>Kind is required on the two tools whose kinds take different arguments. It is
    /// nullable on the record rather than defaulted, so omitting it refuses naming the argument
    /// rather than silently meaning the first member.</summary>
    [Fact]
    public void KindIsRequiredOnCreateAndDelete()
    {
        Assert.Equal("kind", Fault(new CreateArguments("Inbox/Widget.md", null, Content: "")));
        Assert.Equal("kind", Fault(new DeleteArguments("Inbox/Widget.md", null)));
    }

    /// <summary>On <c>move</c> both kinds take the same arguments, so the assertion buys only
    /// fail-fast and a call that makes none is complete.</summary>
    [Fact]
    public void KindIsOptionalOnMove()
    {
        Assert.Null(Fault(new MoveArguments("Inbox/Widget.md", "Projects/Widget.md")));
        Assert.Null(Fault(new MoveArguments("Inbox/Widget.md", "Projects/Widget.md", Kind: OperandKind.Note)));
    }

    /// <summary>An unrecognized enumerated value is refused in this vocabulary rather than through
    /// the binder, which is what <see cref="EnumArgument"/> exists to make possible.</summary>
    [Fact]
    public void AnUnrecognizedKindIsRefusedHereRatherThanByTheBinder()
    {
        var unrecognized = (OperandKind)(-1);

        Assert.Equal("kind", Fault(new CreateArguments("Inbox/Widget.md", unrecognized, Content: "")));
        Assert.Equal("kind", Fault(new DeleteArguments("Inbox/Widget.md", unrecognized)));
        Assert.Equal("kind", Fault(new MoveArguments("Inbox/Widget.md", "Projects/Widget.md", Kind: unrecognized)));
    }

    /// <summary>An unrecognized value arrives through binding rather than throwing there, so the
    /// whole path from the wire to the fault is what a caller actually meets.</summary>
    [Fact]
    public void AnUnrecognizedKindSurvivesBindingSoTheToolCanRefuseIt()
    {
        var bound = NativeToolArguments.Bind(
            typeof(CreateArguments), VaultToolHarness.Args(new { path = "Inbox/Widget.md", kind = "note-ish", content = "" }));

        Assert.Equal("kind", Fault(bound));
    }

    /// <summary>An absent optional kind stays absent rather than binding to an unrecognized
    /// value - the serializer's nullable handling wraps the lenient reader rather than replacing
    /// it.</summary>
    [Fact]
    public void AnAbsentOptionalKindBindsToNothingAtAll()
    {
        var bound = (MoveArguments)NativeToolArguments.Bind(
            typeof(MoveArguments), VaultToolHarness.Args(new { path = "Inbox/Widget.md", destination = "Projects/Widget.md" }));

        Assert.Null(bound.Kind);
        Assert.Null(Fault(bound));
    }

    // ---- Inert parameters ----

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

    /// <summary>A creation composes nothing and templates nothing, so the text is the whole
    /// substance of the call; a caller wanting an empty note says so.</summary>
    [Fact]
    public void ANoteCreationRequiresItsContentAndTakesEmptyTextForAnEmptyNote()
    {
        Assert.Equal("content", Fault(new CreateArguments("Inbox/Widget.md", OperandKind.Note)));
        Assert.Null(Fault(new CreateArguments("Inbox/Widget.md", OperandKind.Note, Content: "")));
    }

    /// <summary>The arguments each kind does take are not faults on the kind that takes them.</summary>
    [Fact]
    public void EachKindTakesItsOwnArguments()
    {
        Assert.Null(Fault(new CreateArguments("Projects/Widget", OperandKind.Folder, GitKeep: true)));
        Assert.Null(Fault(new DeleteArguments("Projects/Widget", OperandKind.Folder, Recursive: true)));
    }

    /// <summary>The gate is the same argument on both tools that carry one, and false by
    /// default.</summary>
    [Fact]
    public void ThePreviewGateIsOffByDefaultOnBothToolsThatCarryOne()
    {
        Assert.False(new DeleteArguments("Inbox/Widget.md", OperandKind.Note).Apply);
        Assert.False(new MoveArguments("Inbox/Widget.md", "Projects/Widget.md").Apply);
    }
}
