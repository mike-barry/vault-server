using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// What may leave a pass scope does not bound. The three passes reach notes the caller's grant
/// cannot read, so the report carries identities and positions and nothing else: a path, a line, and
/// the link target - values the caller already named or the operation itself produced.
///
/// Asserted structurally rather than by inspecting a result, because the constraint is on the shape.
/// A member that could carry surrounding text or a note excerpt would have to be added here first.
/// </summary>
public sealed class LinkReportShapeTests
{
    private static IReadOnlyList<string> MembersOf<T>() =>
        [.. typeof(T).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal)];

    [Fact]
    public void ARewriteCarriesAPathALineAndTheTwoTargets() =>
        Assert.Equal(["From", "Line", "Path", "To"], MembersOf<LinkRewrite>());

    [Fact]
    public void AnOccurrenceCarriesAPathALineAndTheTarget() =>
        Assert.Equal(["Line", "Path", "Target"], MembersOf<LinkOccurrence>());
}
