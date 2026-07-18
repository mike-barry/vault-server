namespace VaultMcpServer.Vault;

/// <summary>
/// Well-known op-class name constants for fixed C# call sites (the chokepoint's internal
/// Read/Write checks). The authoritative, extensible set of valid op-class names is
/// data-driven via <see cref="OpClassRegistry"/> (op-classes.json); op-class values
/// flowing through config/scripts (<c>Grant</c>, <c>ScriptTool</c>) are plain strings
/// validated against that registry, not this enum-turned-constant-holder.
/// </summary>
public static class OpClass
{
    public const string Read = "Read";
    public const string Write = "Write";
}
