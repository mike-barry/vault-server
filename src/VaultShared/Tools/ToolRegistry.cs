using System.Text.Json;

namespace VaultShared.Tools;

/// <summary>
/// A tool whose advertised schema is generated from a vault's loaded frontmatter declaration on top
/// of its argument record, rather than from the record alone.
///
/// <b>It is a type question, so the compiler carries it.</b> A front-end asks every registration for
/// its schema and one line answers both kinds, which is what keeps the projection free of the
/// per-tool branch the front-end boundary forbids.
///
/// The vocabulary is per vault and a listing is not, so the vault is the projection's choice and
/// arrives here as a name.
/// </summary>
public interface IVocabularySchema
{
    JsonElement AdvertisedSchema(string vaultName);
}

/// <summary>
/// Every tool a front-end may project, held as declarations. Registration takes the declaration
/// first and requires it, so a type with none has nothing to register and cannot reach a front-end
/// by implementing an interface alone.
///
/// Names are matched ordinally and are unique: a second registration under a name already taken is a
/// wiring defect, not a precedence rule to invent.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, Registration> _byName = new(StringComparer.Ordinal);
    private readonly List<Registration> _registrations = [];

    /// <summary>A declaration and the instance that answers for it.</summary>
    public readonly record struct Registration(ToolDeclaration Declaration, ITool Tool)
    {
        /// <summary>
        /// What a front-end advertises for this tool: the schema derived from the argument record the
        /// declaration names, or - where the tool generates one over a vault's loaded declaration -
        /// that tool's own rendering for <paramref name="vaultName"/>.
        ///
        /// Both front-ends call this rather than reaching for a tool by name, so neither carries a
        /// per-tool branch and a tool whose schema acquires a vocabulary is covered by implementing
        /// <see cref="IVocabularySchema"/> alone.
        /// </summary>
        public JsonElement AdvertisedSchema(string vaultName) =>
            Tool is IVocabularySchema vocabulary
                ? vocabulary.AdvertisedSchema(vaultName)
                : NativeToolArguments.SchemaFor(Declaration.Arguments);
    }

    /// <summary>Every registration, in registration order - which is the order a front-end lists them
    /// in. The instance rides along with the declaration because a projection needs both: the
    /// declaration for the name, description and argument record, and the instance for a schema the
    /// tool renders itself.</summary>
    public IReadOnlyList<Registration> Registrations => _registrations;

    public ToolRegistry Add(ToolDeclaration declaration, ITool tool)
    {
        var registration = new Registration(declaration, tool);
        if (!_byName.TryAdd(declaration.Name, registration))
            throw new ArgumentException($"A tool is already registered under the name \"{declaration.Name}\".", nameof(declaration));

        _registrations.Add(registration);
        return this;
    }

    /// <summary>The registration under <paramref name="name"/>, or null. A front-end turns the null
    /// into its own refusal, because only it knows whether another tool surface might still carry
    /// the name.</summary>
    public Registration? Find(string name) =>
        _byName.TryGetValue(name, out var registration) ? registration : null;
}
