namespace VaultShared.Tests;

/// <summary>
/// Typo-safe constants for the two built-in op-class names, used by tests to build grants and
/// assert op-class outcomes. Lives in the test project because production no longer references
/// them: op-class enforcement is string-based against the data-driven
/// <see cref="Vault.OpClassRegistry"/> (op-classes.json), and the native tools declare their
/// op class as a string literal. If a future native tool needs a compile-time Read/Write
/// constant, promote this back into the main assembly.
/// </summary>
public static class OpClass
{
    public const string Read = "content:read";
    public const string Write = "content:write";
}
