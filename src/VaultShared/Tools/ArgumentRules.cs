namespace VaultShared.Tools;

/// <summary>
/// Provides the argument checks that more than one argument record needs. Every rule here is
/// decidable from the record alone, without a vault or the filesystem, so the whole argument surface
/// stays refusable before anything is resolved or locked.
/// </summary>
public static class ArgumentRules
{
    /// <summary>Checks that a required argument was supplied and is not blank.</summary>
    /// <param name="value">The bound value.</param>
    /// <param name="argument">The argument's wire name.</param>
    /// <returns>A fault when the value is missing or blank, otherwise null. Binding leaves an
    /// omitted member null rather than refusing, so the requirement is enforced here.</returns>
    public static ArgumentFault? Missing(string? value, string argument) =>
        string.IsNullOrWhiteSpace(value)
            ? new ArgumentFault(argument, $"\"{argument}\" is required.")
            : null;

    /// <summary>Checks that an enumerated argument holds a value the enumeration declares.</summary>
    /// <typeparam name="TEnum">The enumeration type.</typeparam>
    /// <param name="value">The bound value.</param>
    /// <param name="argument">The argument's wire name.</param>
    /// <returns>A fault naming the accepted values when the value is unrecognized, otherwise null.
    /// <see cref="EnumArgument"/> is what lets an unrecognized value reach this check instead of
    /// escaping as a serializer-shaped binding failure.</returns>
    public static ArgumentFault? Unrecognized<TEnum>(TEnum value, string argument) where TEnum : struct, Enum =>
        EnumArgument.IsRecognized(value)
            ? null
            : new ArgumentFault(
                argument,
                $"\"{argument}\" must be one of {string.Join(", ", EnumArgument.WireNames(typeof(TEnum)).Select(name => $"\"{name}\""))}.");
}
