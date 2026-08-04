using VaultShared.Links;

namespace VaultShared.Tests.Links;

/// <summary>
/// Checks the members of the types a link pass reports. A pass reaches notes the caller's grant
/// cannot read, so a report may carry only a path, a line, and the link target - values the caller
/// already named or the operation itself produced - and never note text.
/// </summary>
/// <remarks>
/// The check is on the type's members rather than on a particular result, so a new member that
/// could carry surrounding text or a note excerpt fails these tests until it is listed here.
/// </remarks>
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
