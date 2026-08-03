namespace VaultShared.Tools;

/// <summary>
/// A tool's coded refusal. Raised rather than returned, because it must survive frames that did not
/// expect it: returning one would make surfacing it correctly a convention every caller keeps. A
/// tool catches nothing it did not raise, and no layer re-shapes one on the way past.
///
/// <see cref="Code"/> is drawn from <see cref="ToolCodes"/>, which also states the three rules
/// <see cref="Data"/> obeys.
/// </summary>
public sealed class ToolRefusal(string code, string message, object? data = null) : Exception(message)
{
    /// <summary>The stable code a caller branches on, from <see cref="ToolCodes"/>.</summary>
    public string Code { get; } = code;

    /// <summary>
    /// The refusal's payload, or null where the code carries none. Named apart from
    /// <see cref="Exception.Data"/>, which is an unrelated <see cref="System.Collections.IDictionary"/>
    /// on the base type - hence the <c>new</c>.
    /// </summary>
    public new object? Data { get; } = data;
}
