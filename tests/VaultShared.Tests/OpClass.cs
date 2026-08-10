namespace VaultShared.Tests;

/// <summary>
/// Provides typo-safe constants for the two built-in op-class names, used by tests to build
/// grants and assert op-class outcomes.
/// </summary>
/// <remarks>
/// Op-class enforcement is string-based against the data-driven
/// <see cref="VaultShared.Configuration.OpClassRegistry"/> loaded from op-classes.json, and a tool
/// declares its op class as a string literal, so no production code references these constants.
/// </remarks>
public static class OpClass
{
    /// <summary>The op-class name a tool declares to read content.</summary>
    public const string Read = "content:read";

    /// <summary>The op-class name a tool declares to write content.</summary>
    public const string Write = "content:write";
}
