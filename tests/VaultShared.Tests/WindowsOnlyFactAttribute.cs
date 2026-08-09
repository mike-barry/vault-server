namespace VaultShared.Tests;

/// <summary>
/// Marks a test that runs on Windows and is skipped everywhere else.
/// </summary>
/// <remarks>
/// Use this where the behaviour under test is genuinely Windows-shaped and no cross-platform
/// property stands behind it. A drive-qualified path such as <c>C:\elsewhere\Widget.md</c> is the
/// case that needs it: the platform reads it as rooted on Windows and as an ordinary relative name
/// with a colon in its first segment elsewhere, so the same input is refused on one platform and
/// admitted on the other, and neither answer is a defect.
///
/// A skip is preferred to a test that fails off Windows, which would report a platform difference as
/// a regression, and to a runtime early return, which would report a pass for a test that never ran.
/// The skip is decided in the constructor because xunit reads
/// <see cref="FactAttribute.Skip"/> when it discovers the test rather than when it runs it, and
/// xunit 2.9.3 offers no conditional skip of its own.
/// </remarks>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    /// <summary>Initializes the attribute, skipping the test unless the host is Windows.</summary>
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "The behaviour under test is Windows-shaped and has no cross-platform equivalent.";
    }
}
