using System.Text.Json;
using VaultShared.Configuration;

namespace VaultShared.Frontmatter;

/// <summary>
/// Builds a <see cref="FrontmatterRegistry"/> from a declaration file. Outside tests, which
/// construct one directly, this is the only producer of a registry.
///
/// The declaration is validated at load rather than at first use, so a file the operator got wrong
/// leaves a Degraded host with frontmatter writes refused, rather than a refusal that arrives on
/// the first note that happens to exercise the broken part. Every check runs here, over the file's
/// shape, before the records are built.
///
/// The runtime reads the declaration and never writes it. There is deliberately no writing
/// counterpart to this type, which is what keeps the agent-facing surface from redefining what
/// counts as protected.
/// </summary>
public static class FrontmatterRegistryLoader
{
    /// <summary>Comments and trailing commas are tolerated on read. Comments serve the operator and
    /// are not preserved, which does not matter because nothing rewrites the file. Where a
    /// rationale must reach a display surface, a declaration entry carries an explicit note field
    /// instead.</summary>
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads the declaration at <paramref name="path"/> and returns one of the three states.
    /// Nothing is thrown and nothing is recorded here: what a host does about a failure belongs to
    /// <see cref="FrontmatterRegistries"/>, which knows which vaults named this file.
    /// </summary>
    /// <param name="path">The declaration file to read.</param>
    /// <param name="opClasses">The loaded op-class configuration, for the one cross-file check: a
    /// declaration naming an authority nothing defines. This load runs after the op-class
    /// configuration in the cascade for that reason.</param>
    /// <returns>The declaration state: loaded, absent, or unloadable.</returns>
    public static RegistryState Load(string path, OpClassRegistry opClasses)
    {
        if (!File.Exists(path))
            return new RegistryState.Absent(path);

        try
        {
            return new RegistryState.Loaded(LoadCore(path, opClasses));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            return new RegistryState.Unloadable(path, ex.Message);
        }
    }

    private static FrontmatterRegistry LoadCore(string path, OpClassRegistry opClasses)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), ReadOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("the declaration's root must be an object.");

        foreach (var field in root.EnumerateObject())
            if (field.Name is not ("version" or "namespaces" or "keys" or "entry_schemas"))
                throw new InvalidDataException($"unrecognized top-level key \"{field.Name}\".");

        // A declaration written against a later shape must not be read as this one. Fields it adds
        // would be caught by the unrecognized-key refusal above, but fields it repurposes would
        // not.
        if (root.TryGetProperty("version", out var version) && (version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1))
            throw new InvalidDataException("\"version\" must be 1.");

        var schemas = root.TryGetProperty("entry_schemas", out var schemasElement)
            ? ParseSchemas(schemasElement)
            : new Dictionary<string, EntrySchema>(StringComparer.OrdinalIgnoreCase);

        var namespaces = root.TryGetProperty("namespaces", out var namespacesElement)
            ? ParseNamespaces(namespacesElement, opClasses)
            : [];

        var keys = root.TryGetProperty("keys", out var keysElement)
            ? ParseKeys(keysElement, schemas, opClasses)
            : new Dictionary<string, KeyDeclaration>(StringComparer.OrdinalIgnoreCase);

        return new FrontmatterRegistry(keys, namespaces, schemas);
    }

    // ---- Keys ----

    private static Dictionary<string, KeyDeclaration> ParseKeys(
        JsonElement element, Dictionary<string, EntrySchema> schemas, OpClassRegistry opClasses)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("\"keys\" must be an object of key name to declaration.");

        var keys = new Dictionary<string, KeyDeclaration>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Name.Length == 0)
                throw new InvalidDataException("a declared key has an empty name.");
            if (keys.ContainsKey(entry.Name))
                throw new InvalidDataException($"key \"{entry.Name}\" is declared twice - key names are matched case-insensitively.");

            keys[entry.Name] = ParseKey(entry.Name, entry.Value, schemas, opClasses);
        }

        return keys;
    }

    private static KeyDeclaration ParseKey(
        string name, JsonElement element, Dictionary<string, EntrySchema> schemas, OpClassRegistry opClasses)
    {
        var where = $"key \"{name}\"";
        Require(element.ValueKind == JsonValueKind.Object, $"{where} must be an object.");
        Allow(element, where, "shape", "value", "entry", "write_requires", "note");

        var shape = Text(element, "shape", where) switch
        {
            "scalar" => KeyShape.Scalar,
            "block_list" => KeyShape.BlockList,
            null => throw new InvalidDataException($"{where} declares no \"shape\" - it is scalar or block_list."),
            var other => throw new InvalidDataException($"{where} has an unrecognized shape \"{other}\" - it is scalar or block_list."),
        };

        var hasValue = element.TryGetProperty("value", out var valueElement);
        var hasEntry = element.TryGetProperty("entry", out var entryElement);

        // Both directions of the shape-and-constraint contradiction. A block-list key's constraints
        // live on its entry schema, and a scalar key has no members for one to describe.
        if (shape == KeyShape.BlockList && hasValue)
            throw new InvalidDataException($"{where} is a block_list and declares a \"value\" constraint - its entry schema carries the constraints.");
        if (shape == KeyShape.Scalar && hasEntry)
            throw new InvalidDataException($"{where} is a scalar and names an \"entry\" schema - only a block_list holds entries.");

        EntrySchema? schema = null;
        if (shape == KeyShape.BlockList)
        {
            var schemaName = hasEntry
                ? entryElement.GetString() ?? throw new InvalidDataException($"{where} has a non-string \"entry\".")
                : throw new InvalidDataException($"{where} is a block_list and names no \"entry\" schema.");
            // Resolved to the instance here, so two keys naming one schema hold one object.
            if (!schemas.TryGetValue(schemaName, out schema))
                throw new InvalidDataException($"{where} names entry schema \"{schemaName}\", which is not declared.");
        }

        var constraint = hasValue ? ParseConstraint(valueElement, where) : null;
        var writeRequires = OpClass(element, where, opClasses);

        return new KeyDeclaration(shape, constraint, schema, writeRequires) { Note = Text(element, "note", where) };
    }

    // ---- Namespaces ----

    private static List<NamespaceRule> ParseNamespaces(JsonElement element, OpClassRegistry opClasses)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("\"namespaces\" must be an array of prefix rules.");

        var rules = new List<NamespaceRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in element.EnumerateArray())
        {
            const string where = "a namespace rule";
            Require(entry.ValueKind == JsonValueKind.Object, $"{where} must be an object.");
            Allow(entry, where, "prefix", "write_requires", "note");

            var prefix = Text(entry, "prefix", where) is { Length: > 0 } p
                ? p
                : throw new InvalidDataException($"{where} declares no non-empty \"prefix\".");
            // A namespace exists to name an authority. One that names none says nothing, because a
            // namespace can only add to what is required.
            var requires = OpClass(entry, $"namespace \"{prefix}\"", opClasses)
                ?? throw new InvalidDataException($"namespace \"{prefix}\" declares no \"write_requires\" - a namespace rule exists to name one.");
            if (!seen.Add(prefix))
                throw new InvalidDataException($"namespace \"{prefix}\" is declared twice.");

            rules.Add(new NamespaceRule(prefix, requires) { Note = Text(entry, "note", where) });
        }

        return rules;
    }

    // ---- Entry schemas ----

    private static Dictionary<string, EntrySchema> ParseSchemas(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("\"entry_schemas\" must be an object of schema name to schema.");

        var schemas = new Dictionary<string, EntrySchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in element.EnumerateObject())
        {
            if (schemas.ContainsKey(entry.Name))
                throw new InvalidDataException($"entry schema \"{entry.Name}\" is declared twice.");
            schemas[entry.Name] = ParseSchema(entry.Name, entry.Value);
        }

        return schemas;
    }

    private static EntrySchema ParseSchema(string name, JsonElement element)
    {
        var where = $"entry schema \"{name}\"";
        Require(element.ValueKind == JsonValueKind.Object, $"{where} must be an object.");
        Allow(element, where, "variants");

        if (!element.TryGetProperty("variants", out var variantsElement) || variantsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{where} must declare a \"variants\" array.");

        var variants = new List<EntryVariant>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discriminators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawScalar = false;

        foreach (var variantElement in variantsElement.EnumerateArray())
        {
            var variant = ParseVariant(variantElement, where);
            if (!names.Add(variant.Name))
                throw new InvalidDataException($"{where} declares two variants named \"{variant.Name}\".");

            // Both halves of the rule that an entry matches exactly one variant. Matching walks the
            // variants once, so two variants that answer to the same node would be a declaration
            // nothing can match, and there is no precedence rule to fall back on.
            if (variant.DiscriminatedBy is { } discriminator && !discriminators.Add(discriminator))
                throw new InvalidDataException($"{where} has two variants discriminated by \"{discriminator}\".");
            if (variant.Scalar is not null)
            {
                if (sawScalar)
                    throw new InvalidDataException($"{where} declares two scalar variants - a bare value would match both.");
                sawScalar = true;
            }

            variants.Add(variant);
        }

        if (variants.Count == 0)
            throw new InvalidDataException($"{where} declares no variants.");

        return new EntrySchema(variants);
    }

    private static EntryVariant ParseVariant(JsonElement element, string schemaWhere)
    {
        Require(element.ValueKind == JsonValueKind.Object, $"{schemaWhere} has a variant that is not an object.");
        var name = Text(element, "name", schemaWhere) is { Length: > 0 } n
            ? n
            : throw new InvalidDataException($"{schemaWhere} has a variant with no \"name\".");

        var where = $"{schemaWhere} variant \"{name}\"";
        Allow(element, where,
            "name", "discriminated_by", "scalar", "fields", "exactly_one_of", "identity", "identity_match",
            "removable_wholesale", "note");

        var fields = ParseFields(element, where);

        var discriminatedBy = Text(element, "discriminated_by", where);
        var scalar = element.TryGetProperty("scalar", out var scalarElement) ? ParseConstraint(scalarElement, where) : null;

        // The selector is one or the other. A variant declaring both would be reachable two ways
        // and one declaring neither would be unreachable, and matching can resolve neither case.
        if (discriminatedBy is not null && scalar is not null)
            throw new InvalidDataException($"{where} declares both \"discriminated_by\" and \"scalar\" - a selector is one or the other.");
        if (discriminatedBy is null && scalar is null)
            throw new InvalidDataException($"{where} declares neither \"discriminated_by\" nor \"scalar\" - nothing selects it.");
        if (scalar is not null && fields.Count > 0)
            throw new InvalidDataException($"{where} is a scalar variant and declares fields - a scalar variant is a plain value.");
        if (discriminatedBy is not null && !fields.ContainsKey(discriminatedBy))
            throw new InvalidDataException($"{where} is discriminated by \"{discriminatedBy}\", which it does not declare as a field.");

        var exactlyOneOf = FieldNames(element, "exactly_one_of", where, fields);
        if (exactlyOneOf.Count == 1)
            throw new InvalidDataException($"{where} has an \"exactly_one_of\" group of one field - declare it required instead.");

        var identity = FieldNames(element, "identity", where, fields);
        var identityMatch = ParseIdentityMatch(element, where, identity.Count > 0);

        var removable = element.TryGetProperty("removable_wholesale", out var removableElement)
            ? removableElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException($"{where} has a non-boolean \"removable_wholesale\"."),
            }
            : false;

        return new EntryVariant(name, discriminatedBy, scalar, fields, exactlyOneOf, identity, identityMatch, removable)
        {
            Note = Text(element, "note", where),
        };
    }

    private static Dictionary<string, EntryField> ParseFields(JsonElement variant, string where)
    {
        var fields = new Dictionary<string, EntryField>(StringComparer.OrdinalIgnoreCase);
        if (!variant.TryGetProperty("fields", out var element))
            return fields;
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where} has a \"fields\" that is not an object.");

        foreach (var entry in element.EnumerateObject())
        {
            var fieldWhere = $"{where} field \"{entry.Name}\"";
            Require(entry.Name.Length > 0, $"{where} has a field with an empty name.");
            Require(entry.Value.ValueKind == JsonValueKind.Object, $"{fieldWhere} must be an object.");
            Allow(entry.Value, fieldWhere, "required", "value", "note");
            if (fields.ContainsKey(entry.Name))
                throw new InvalidDataException($"{where} declares field \"{entry.Name}\" twice.");

            var required = entry.Value.TryGetProperty("required", out var requiredElement)
                ? requiredElement.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new InvalidDataException($"{fieldWhere} has a non-boolean \"required\"."),
                }
                : false;

            var constraint = entry.Value.TryGetProperty("value", out var valueElement)
                ? ParseConstraint(valueElement, fieldWhere)
                : null;

            fields[entry.Name] = new EntryField(required, constraint) { Note = Text(entry.Value, "note", fieldWhere) };
        }

        return fields;
    }

    /// <summary>
    /// Reads a list of the variant's own field names, canonicalized to the spelling the variant
    /// declares. Naming a field the variant does not declare fails the load wherever it happens,
    /// because an identity or an exclusive group over a field that is not there addresses nothing.
    /// </summary>
    /// <param name="variant">The variant object being read.</param>
    /// <param name="property">Which property holds the list, such as <c>identity</c>.</param>
    /// <param name="where">How the variant is named in a failure message.</param>
    /// <param name="fields">The variant's declared fields, for canonicalizing and checking each
    /// name.</param>
    /// <returns>The canonical field names, empty where the property is absent.</returns>
    /// <exception cref="InvalidDataException">The property is not an array of declared field names,
    /// repeats a name, or is present and empty.</exception>
    private static List<string> FieldNames(
        JsonElement variant, string property, string where, Dictionary<string, EntryField> fields)
    {
        var names = new List<string>();
        if (!variant.TryGetProperty(property, out var element))
            return names;
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{where} has a \"{property}\" that is not an array of field names.");

        foreach (var item in element.EnumerateArray())
        {
            var declared = item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new InvalidDataException($"{where} has a non-string entry in \"{property}\".");
            if (!fields.TryGetValue(declared, out _))
                throw new InvalidDataException($"{where} names \"{declared}\" in \"{property}\", which it does not declare as a field.");
            var canonical = fields.Keys.First(k => k.Equals(declared, StringComparison.OrdinalIgnoreCase));
            if (names.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"{where} names \"{declared}\" twice in \"{property}\".");
            names.Add(canonical);
        }

        if (names.Count == 0)
            throw new InvalidDataException($"{where} has an empty \"{property}\".");

        return names;
    }

    private static StringComparison ParseIdentityMatch(JsonElement variant, string where, bool hasIdentity)
    {
        var declared = Text(variant, "identity_match", where);
        if (declared is null)
            return hasIdentity
                // Required alongside an identity, because no default is right: an identity naming a
                // file compares the way the filesystem resolves it, and a line of prose compares
                // exactly. Guessing either would make a scan and an operation disagree about
                // whether an entry is already there.
                ? throw new InvalidDataException($"{where} declares an identity and no \"identity_match\" - it is case-sensitive or case-insensitive.")
                : StringComparison.Ordinal;

        return declared switch
        {
            "case-sensitive" => StringComparison.Ordinal,
            "case-insensitive" => StringComparison.OrdinalIgnoreCase,
            _ => throw new InvalidDataException($"{where} has an unrecognized \"identity_match\" \"{declared}\" - it is case-sensitive or case-insensitive."),
        };
    }

    // ---- Value constraints ----

    private static ValueConstraint ParseConstraint(JsonElement element, string where)
    {
        Require(element.ValueKind == JsonValueKind.Object, $"{where} has a value constraint that is not an object.");
        var kind = Text(element, "kind", where)
            ?? throw new InvalidDataException($"{where} has a value constraint with no \"kind\".");

        switch (kind)
        {
            case "date":
                Allow(element, where, "kind", "format");
                var format = Text(element, "format", where);
                Require(format is not "", $"{where} has a date constraint with an empty \"format\".");
                return new DateConstraint(format ?? DateConstraint.IsoDate);

            case "enum":
                Allow(element, where, "kind", "values");
                return new EnumerationConstraint(ParseEnumValues(element, where));

            case "charclass":
                Allow(element, where, "kind", "chars", "min", "max");
                return ParseCharClass(element, where);

            case "wikilink":
                Allow(element, where, "kind");
                return new WikilinkConstraint();

            case "string":
                Allow(element, where, "kind");
                return new StringConstraint();

            default:
                // Keeps the set of kinds closed. A misspelled kind read as an unconstrained string
                // would leave a field the declaration believes is checked.
                throw new InvalidDataException(
                    $"{where} names an unknown constraint kind \"{kind}\" - the kinds are date, enum, charclass, wikilink and string.");
        }
    }

    private static List<string> ParseEnumValues(JsonElement element, string where)
    {
        if (!element.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{where} has an enum constraint with no \"values\" array.");

        var members = new List<string>();
        foreach (var item in values.EnumerateArray())
        {
            var member = item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new InvalidDataException($"{where} has a non-string enum value.");
            if (members.Contains(member, StringComparer.Ordinal))
                throw new InvalidDataException($"{where} lists enum value \"{member}\" twice.");
            members.Add(member);
        }

        // An enumeration with no members admits nothing, so every value of the field it constrains
        // would be a violation.
        if (members.Count == 0)
            throw new InvalidDataException($"{where} has an enum constraint with no values.");

        return members;
    }

    private static CharClassConstraint ParseCharClass(JsonElement element, string where)
    {
        var chars = Text(element, "chars", where)
            ?? throw new InvalidDataException($"{where} has a charclass constraint with no \"chars\".");
        var characterClass = CharacterClasses.Parse(chars)
            ?? throw new InvalidDataException(
                $"{where} names an unknown character class \"{chars}\" - the classes are {string.Join(", ", CharacterClasses.Names)}.");

        var min = Bound(element, "min", where);
        var max = Bound(element, "max", where);
        Require(min >= 0, $"{where} has a negative \"min\".");
        // Crossed bounds admit nothing at all, the same problem as an empty enumeration.
        Require(min <= max, $"{where} has a charclass whose bounds cross - \"min\" {min} is above \"max\" {max}.");

        return new CharClassConstraint(characterClass, min, max);
    }

    private static int Bound(JsonElement element, string property, string where) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var bound)
            ? bound
            : throw new InvalidDataException($"{where} has a charclass with no whole-number \"{property}\".");

    // ---- Shared checks ----

    /// <summary>
    /// Reads the declared op class at <c>write_requires</c>. This is the cross-file check: an
    /// authority the op-class configuration does not define is a protection nothing can ever
    /// satisfy, which would look like a permanently locked key rather than the misconfiguration it
    /// is.
    /// </summary>
    /// <param name="element">The object that may carry <c>write_requires</c>.</param>
    /// <param name="where">How the object is named in a failure message.</param>
    /// <param name="opClasses">The loaded op-class configuration to check against.</param>
    /// <returns>The canonical op class name, or null where none is declared.</returns>
    /// <exception cref="InvalidDataException">The value is not a non-empty string, or names an op
    /// class the configuration does not define.</exception>
    private static string? OpClass(JsonElement element, string where, OpClassRegistry opClasses)
    {
        if (Text(element, "write_requires", where) is not { } declared)
            return null;
        if (declared.Length == 0)
            throw new InvalidDataException($"{where} has an empty \"write_requires\".");
        if (!opClasses.IsValid(declared))
            throw new InvalidDataException($"{where} requires op class \"{declared}\", which the op-class configuration does not define.");

        return opClasses.Canonicalize(declared) ?? declared;
    }

    /// <summary>Refuses any key of an object that is not on the permitted list, so a misspelled
    /// field cannot read as a declaration someone made. The other configuration parsers take the
    /// same posture.</summary>
    /// <param name="element">The object whose keys are checked.</param>
    /// <param name="where">How the object is named in a failure message.</param>
    /// <param name="permitted">The key names the object may carry.</param>
    /// <exception cref="InvalidDataException">The object carries a key that is not permitted.</exception>
    private static void Allow(JsonElement element, string where, params string[] permitted)
    {
        foreach (var field in element.EnumerateObject())
            if (!permitted.Contains(field.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"{where} has an unrecognized key \"{field.Name}\".");
    }

    private static string? Text(JsonElement element, string property, string where) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : throw new InvalidDataException($"{where} has a non-string \"{property}\".")
            : null;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
