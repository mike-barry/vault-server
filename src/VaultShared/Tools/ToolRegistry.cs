using System.Text.Json;

namespace VaultShared.Tools;

/// <summary>
/// Implemented by a tool whose advertised schema is generated from a vault's loaded frontmatter
/// declaration on top of its argument record, rather than from the record alone.
/// </summary>
/// <remarks>
/// A front-end asks every registration for its schema, and the implementation answers both kinds, so
/// no front-end carries a per-tool branch. The declared vocabulary differs per vault while a tool
/// listing does not, so the projection chooses the vault and passes its name in.
/// </remarks>
public interface IVocabularySchema
{
    /// <summary>Returns the schema to advertise for this tool in the named vault.</summary>
    /// <param name="vaultName">The vault whose declared vocabulary shapes the schema.</param>
    /// <returns>The advertised JSON schema.</returns>
    JsonElement AdvertisedSchema(string vaultName);
}

/// <summary>
/// Holds every tool a front-end may project, each with its declaration. Registration requires the
/// declaration, so a type without one cannot reach a front-end by implementing
/// <see cref="ITool"/> alone.
/// </summary>
/// <remarks>
/// Names are compared ordinally and must be unique. A second registration under a name already taken
/// throws rather than defining a precedence rule.
/// </remarks>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, Registration> _byName = new(StringComparer.Ordinal);
    private readonly List<Registration> _registrations = [];

    /// <summary>Pairs a declaration with the tool instance that answers for it.</summary>
    /// <param name="Declaration">The tool's declaration.</param>
    /// <param name="Tool">The instance that runs the tool.</param>
    public readonly record struct Registration(ToolDeclaration Declaration, ITool Tool)
    {
        /// <summary>
        /// Returns the schema a front-end advertises for this tool.
        /// </summary>
        /// <param name="vaultName">The vault a vocabulary-derived schema is rendered for.</param>
        /// <returns>The tool's own rendering where it implements <see cref="IVocabularySchema"/>,
        /// otherwise the schema derived from the argument record the declaration names.</returns>
        public JsonElement AdvertisedSchema(string vaultName) =>
            Tool is IVocabularySchema vocabulary
                ? vocabulary.AdvertisedSchema(vaultName)
                : NativeToolArguments.SchemaFor(Declaration.Arguments);
    }

    /// <summary>Every registration, in registration order, which is the order a front-end lists them
    /// in.</summary>
    public IReadOnlyList<Registration> Registrations => _registrations;

    /// <summary>Registers a tool under its declared name.</summary>
    /// <param name="declaration">The tool's declaration.</param>
    /// <param name="tool">The instance that runs the tool.</param>
    /// <returns>This registry, so registrations can be chained.</returns>
    /// <exception cref="ArgumentException">A tool is already registered under that name.</exception>
    public ToolRegistry Add(ToolDeclaration declaration, ITool tool)
    {
        var registration = new Registration(declaration, tool);
        if (!_byName.TryAdd(declaration.Name, registration))
            throw new ArgumentException($"A tool is already registered under the name \"{declaration.Name}\".", nameof(declaration));

        _registrations.Add(registration);
        return this;
    }

    /// <summary>Returns the registration under the given name, or null when there is none. The
    /// front-end turns a null into its own refusal, because only it knows whether another tool
    /// surface might still carry the name.</summary>
    /// <param name="name">The tool's wire name, compared ordinally.</param>
    /// <returns>The registration, or null.</returns>
    public Registration? Find(string name) =>
        _byName.TryGetValue(name, out var registration) ? registration : null;
}
