namespace VaultShared.Tools;

/// <summary>
/// The pure argument rules more than one record states, as faults rather than as prose repeated per
/// tool. Everything here is decidable from the record alone - no vault, no filesystem - which is what
/// keeps the whole argument surface refusable before anything is resolved or locked.
/// </summary>
public static class ArgumentRules
{
    /// <summary>
    /// An argument the advertised schema marks required, absent or blank. Binding leaves an omitted
    /// member null rather than refusing, so the schema states the requirement and this states it.
    /// </summary>
    public static ArgumentFault? Missing(string? value, string argument) =>
        string.IsNullOrWhiteSpace(value)
            ? new ArgumentFault(argument, $"\"{argument}\" is required.")
            : null;

    /// <summary>
    /// An enumerated value the vocabulary does not carry, named with the values it does.
    /// <see cref="EnumArgument"/> is what lets such a value arrive here at all instead of escaping
    /// as a binder failure shaped by the serializer.
    /// </summary>
    public static ArgumentFault? Unrecognized<TEnum>(TEnum value, string argument) where TEnum : struct, Enum =>
        EnumArgument.IsRecognized(value)
            ? null
            : new ArgumentFault(
                argument,
                $"\"{argument}\" must be one of {string.Join(", ", EnumArgument.WireNames(typeof(TEnum)).Select(name => $"\"{name}\""))}.");
}
