using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// Reads the results of a wikilink pass for the tests. Rewrites and occurrences are rendered as
/// one line each, so a failure names the note, the line and the target texts instead of showing a
/// structural diff of a record. The rendered lists are sorted, because the order a pass visits
/// notes in is the engine's and is not part of the contract.
/// </summary>
internal static class LinkPass
{
    /// <summary>Asserts the pass completed and returns its plan.</summary>
    /// <param name="outcome">The outcome of a wikilink pass.</param>
    /// <returns>The plan the pass produced.</returns>
    public static WikilinkPlan Plan(LinkPassOutcome<WikilinkPlan> outcome) =>
        Assert.IsType<LinkPassOutcome<WikilinkPlan>.Completed>(outcome).Result;

    /// <summary>Renders the rewrites of a completed pass's plan.</summary>
    /// <param name="outcome">The outcome of a wikilink pass.</param>
    /// <returns>One sorted line per rewrite.</returns>
    public static IReadOnlyList<string> Rewrites(LinkPassOutcome<WikilinkPlan> outcome) =>
        Rendered(Plan(outcome));

    /// <summary>Renders a plan's rewrites.</summary>
    /// <param name="plan">The plan to render.</param>
    /// <returns>One sorted line per rewrite.</returns>
    public static IReadOnlyList<string> Rendered(WikilinkPlan plan) => Render(plan.Rewrites);

    /// <summary>
    /// Renders a set of rewrites, for a test whose subject is which part of a plan a rewrite
    /// landed in.
    /// </summary>
    /// <param name="rewrites">The rewrites to render.</param>
    /// <returns>One sorted line per rewrite, as "path:line from -&gt; to".</returns>
    public static IReadOnlyList<string> Render(IEnumerable<LinkRewrite> rewrites) =>
        [.. rewrites.Select(r => $"{r.Path}:{r.Line} {r.From} -> {r.To}").Order(StringComparer.Ordinal)];

    /// <summary>Renders a completed pass's occurrences in sorted order.</summary>
    /// <param name="outcome">The outcome of a wikilink pass.</param>
    /// <returns>One sorted line per occurrence.</returns>
    public static IReadOnlyList<string> Occurrences(LinkPassOutcome<IReadOnlyList<LinkOccurrence>> outcome) =>
        [.. InOrder(outcome).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Renders a completed pass's occurrences in the order the pass reported them. Only a test
    /// about position order within a single note should use this, because the order across notes
    /// is the engine's.
    /// </summary>
    /// <param name="outcome">The outcome of a wikilink pass.</param>
    /// <returns>One line per occurrence, as "path:line target".</returns>
    public static IReadOnlyList<string> InOrder(LinkPassOutcome<IReadOnlyList<LinkOccurrence>> outcome) =>
        [.. Assert.IsType<LinkPassOutcome<IReadOnlyList<LinkOccurrence>>.Completed>(outcome).Result
            .Select(o => $"{o.Path}:{o.Line} {o.Target}")];

    /// <summary>Applies a plan, asserts every rewrite landed, and renders the plan.</summary>
    /// <param name="vault">The scratch vault to apply the plan to.</param>
    /// <param name="plan">The plan to apply.</param>
    /// <returns>One sorted line per rewrite.</returns>
    public static IReadOnlyList<string> Apply(ScratchVault vault, WikilinkPlan plan)
    {
        Assert.IsType<LinkApplyOutcome.Applied>(WikilinkMaintenance.Apply(vault.Access.BypassingScope(), plan));
        return Rendered(plan);
    }
}
