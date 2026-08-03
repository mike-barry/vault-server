using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// What the wikilink cases read out of a pass. A rewrite and an occurrence are compared as one
/// rendered line each, so a failure names the note, the position and the two target texts rather
/// than a record's structural diff, and so a set can be sorted - enumeration order is the engine's.
/// </summary>
internal static class LinkPass
{
    public static WikilinkPlan Plan(LinkPassOutcome<WikilinkPlan> outcome) =>
        Assert.IsType<LinkPassOutcome<WikilinkPlan>.Completed>(outcome).Result;

    public static IReadOnlyList<string> Rewrites(LinkPassOutcome<WikilinkPlan> outcome) =>
        Rendered(Plan(outcome));

    public static IReadOnlyList<string> Rendered(WikilinkPlan plan) => Render(plan.Rewrites);

    /// <summary>The same rendering over one of a plan's two halves, for a case whose subject is
    /// which half a rewrite landed in.</summary>
    public static IReadOnlyList<string> Render(IEnumerable<LinkRewrite> rewrites) =>
        [.. rewrites.Select(r => $"{r.Path}:{r.Line} {r.From} -> {r.To}").Order(StringComparer.Ordinal)];

    public static IReadOnlyList<string> Occurrences(LinkPassOutcome<IReadOnlyList<LinkOccurrence>> outcome) =>
        [.. InOrder(outcome).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The occurrences as the pass reported them. Only a case about position order within one note
    /// reads this; across notes the order is the engine's.
    /// </summary>
    public static IReadOnlyList<string> InOrder(LinkPassOutcome<IReadOnlyList<LinkOccurrence>> outcome) =>
        [.. Assert.IsType<LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.Completed>(outcome).Result
            .Select(o => $"{o.Path}:{o.Line} {o.Target}")];

    /// <summary>Applies a plan and asserts it landed whole, answering the same rendering.</summary>
    public static IReadOnlyList<string> Apply(ScratchVault vault, WikilinkPlan plan)
    {
        Assert.IsType<LinkApplyOutcome.Applied>(WikilinkMaintenance.Apply(vault.Access.BypassingScope(), plan));
        return Rendered(plan);
    }
}
