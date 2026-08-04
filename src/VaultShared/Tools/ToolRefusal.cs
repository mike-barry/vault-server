namespace VaultShared.Tools;

/// <summary>
/// Represents a tool's coded refusal, carrying a code from <see cref="ToolCodes"/> and an optional
/// payload.
/// </summary>
/// <remarks>
/// A refusal is thrown rather than returned so that it travels intact through frames that did not
/// expect it; returning one would make surfacing it correctly a convention every caller has to keep.
/// A tool catches nothing it did not raise, and no layer reshapes a refusal on the way past.
/// <see cref="ToolCodes"/> states the three rules <see cref="Data"/> obeys.
/// </remarks>
/// <param name="code">The stable code from <see cref="ToolCodes"/>.</param>
/// <param name="message">The caller-visible message.</param>
/// <param name="data">The payload, or null where the code carries none.</param>
public sealed class ToolRefusal(string code, string message, object? data = null) : Exception(message)
{
    /// <summary>The stable code a caller branches on, from <see cref="ToolCodes"/>.</summary>
    public string Code { get; } = code;

    /// <summary>
    /// The refusal's payload, or null where the code carries none. This deliberately hides
    /// <see cref="Exception.Data"/>, an unrelated <see cref="System.Collections.IDictionary"/> on the
    /// base type, which is why it is declared <c>new</c>.
    /// </summary>
    public new object? Data { get; } = data;
}
