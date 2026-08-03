namespace VaultShared.Tests;

/// <summary>
/// Typo-safe constants for the two built-in op-class names, used by tests to build grants and
/// assert op-class outcomes. Lives in the test project rather than the main assembly because
/// nothing in production references them: op-class enforcement is string-based against the
/// data-driven <see cref="Vault.OpClassRegistry"/> (op-classes.json), and a tool declares its
/// op class as a string literal. A tool needing a compile-time Read/Write constant is the case
/// for promoting this into the main assembly.
/// </summary>
public static class OpClass
{
    public const string Read = "content:read";
    public const string Write = "content:write";
}
