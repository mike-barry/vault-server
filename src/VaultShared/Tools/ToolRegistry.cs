namespace VaultShared.Tools;

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
    private readonly List<ToolDeclaration> _declarations = [];

    /// <summary>A declaration and the instance that answers for it.</summary>
    public readonly record struct Registration(ToolDeclaration Declaration, ITool Tool);

    /// <summary>Every declaration, in registration order - which is the order a front-end lists
    /// them in.</summary>
    public IReadOnlyList<ToolDeclaration> Declarations => _declarations;

    public ToolRegistry Add(ToolDeclaration declaration, ITool tool)
    {
        if (!_byName.TryAdd(declaration.Name, new Registration(declaration, tool)))
            throw new ArgumentException($"A tool is already registered under the name \"{declaration.Name}\".", nameof(declaration));

        _declarations.Add(declaration);
        return this;
    }

    /// <summary>The registration under <paramref name="name"/>, or null. A front-end turns the null
    /// into its own refusal, because only it knows whether another tool surface might still carry
    /// the name.</summary>
    public Registration? Find(string name) =>
        _byName.TryGetValue(name, out var registration) ? registration : null;
}
