using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace VaultShared.Frontmatter;

/// <summary>Describes what a frontmatter key holds. Reading the key's value cannot answer this.</summary>
public enum FrontmatterShape
{
    /// <summary>The key is not in the block.</summary>
    Absent,

    /// <summary>
    /// The key holds a single value, or holds none at all. A key with no value counts as scalar
    /// because there is no list under it to lose; the validator reports it separately as an empty
    /// block.
    /// </summary>
    Scalar,

    /// <summary>The key holds a sequence or a nested mapping.</summary>
    Block,
}

/// <summary>Describes the result of parsing a block's inner text.</summary>
public enum TreeParse
{
    /// <summary>The text parsed to a mapping, which is the tree.</summary>
    Parsed,

    /// <summary>The text is not YAML. A reader treats this as no block; the write path refuses it.</summary>
    Unparsable,

    /// <summary>The text parsed to something other than a mapping, such as a bare scalar or a sequence.</summary>
    NotAMapping,
}

/// <summary>
/// Locates a point in a block's text. Line and column are 1-based and count characters rather than
/// bytes, which is how YAML parsers report a position.
/// </summary>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column within that line.</param>
public readonly record struct ParsePosition(int Line, int Column);

/// <summary>
/// Represents a parsed frontmatter block over YamlDotNet's representation model - the mutable node
/// tree, never its object serializer. Only the node layer preserves key order and per-node quoting
/// style, which this layer's preservation guarantees require. The library and the node layer were
/// both settled by measuring candidate libraries against the real vault's existing notes, and that
/// measurement cannot be repeated from this repository.
///
/// The wrapper keeps raw YamlDotNet nodes off the public surface. It owns canonical key lookup, the
/// shape query, and the reads that tolerate a malformed block, so nothing else decides how a key is
/// matched or what a value's shape is.
///
/// No value is coerced. A date-shaped scalar is the text that was written, because the node layer
/// carries a scalar's text and style rather than a typed value.
/// </summary>
public sealed class FrontmatterTree
{
    /// <summary>
    /// How key names are compared everywhere in this type: lookup, canonicalization, shape, and the
    /// reads. Two spellings of one key are one key, matching how the rest of the layer compares
    /// names.
    /// </summary>
    private const StringComparison KeyComparison = StringComparison.OrdinalIgnoreCase;

    private readonly YamlMappingNode root;

    private FrontmatterTree(YamlMappingNode root) => this.root = root;

    /// <summary>Gets the node the composer emits.</summary>
    internal YamlMappingNode Root => root;

    /// <summary>Returns a block with no keys, which is what a note with no frontmatter parses to
    /// and what a removal of the last key empties to.</summary>
    /// <returns>An empty tree.</returns>
    public static FrontmatterTree Empty() => new(new YamlMappingNode());

    /// <inheritdoc cref="TryParse(string, out FrontmatterTree, out ParsePosition?)"/>
    public static TreeParse TryParse(string innerText, out FrontmatterTree tree) =>
        TryParse(innerText, out tree, out _);

    /// <summary>
    /// Parses a block's inner text. The two failure results are kept apart because the write path
    /// refuses them for different reasons: text that is not YAML, and text that is YAML but not a
    /// mapping.
    /// </summary>
    /// <param name="innerText">The block's inner text, without fences.</param>
    /// <param name="tree">The parsed tree, or an empty tree on either failure, so no caller holds a
    /// null.</param>
    /// <param name="failedAt">Where the parse failed, and null on every result but
    /// <see cref="TreeParse.Unparsable"/>. Text that parsed to the wrong kind of node failed at no
    /// particular point in itself.</param>
    /// <returns>What came of the parse.</returns>
    public static TreeParse TryParse(string innerText, out FrontmatterTree tree, out ParsePosition? failedAt)
    {
        tree = Empty();
        failedAt = null;

        YamlStream stream = new();
        try
        {
            // The split hands over the block's text without the final inner line's terminator, and
            // a block scalar reads its trailing newline from that terminator. Parsing the text as
            // given would turn `desc: |` into `desc: |-` on the way back out. Restoring the
            // terminator here makes this parse and the composer, which always terminates the block,
            // inverses of each other.
            stream.Load(new StringReader(innerText.EndsWith('\n') ? innerText : innerText + "\n"));
        }
        catch (YamlException problem)
        {
            // The mark is read into plain integers here rather than being handed out. No YamlDotNet
            // type crosses this type's surface, so a caller cannot come to depend on the library
            // through a position it was given.
            failedAt = new ParsePosition((int)problem.Start.Line, (int)problem.Start.Column);
            return TreeParse.Unparsable;
        }

        // An empty fence pair is a block with empty frontmatter. Nothing here emits one, but one
        // found on disk reads and edits normally.
        if (stream.Documents.Count == 0)
            return TreeParse.Parsed;

        if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
            return TreeParse.NotAMapping;

        tree = new FrontmatterTree(mapping);
        return TreeParse.Parsed;
    }

    /// <summary>
    /// Reads a whole note's content and returns its frontmatter block, treating a block that will
    /// not parse as no block at all.
    /// </summary>
    /// <param name="content">The note's full content.</param>
    /// <returns>The parsed block, or an empty tree where the note has no block or its block will
    /// not parse.</returns>
    /// <remarks>
    /// Reads tolerate a malformed block and writes do not, and the asymmetry is deliberate. The
    /// read accessors report on notes rather than gating them and run over every file in the vault,
    /// so one malformed note must not fail a whole-vault pass. The write path refuses that same
    /// note through <see cref="TryParse(string, out FrontmatterTree)"/>, because writing into a
    /// block nobody could parse means guessing.
    /// </remarks>
    public static FrontmatterTree Read(string content)
    {
        var split = NoteSplit.Of(content);
        if (!split.HasBlock)
            return Empty();

        TryParse(split.InnerText, out var tree);
        return tree;
    }

    /// <summary>Gets whether the block holds no keys. An empty tree emits no fence.</summary>
    public bool IsEmpty => root.Children.Count == 0;

    /// <summary>Gets every key, in the order the block carries them.</summary>
    public IReadOnlyList<string> Keys => [.. root.Children.Keys.Select(Spelling)];

    /// <summary>Returns whether the block carries this key under any spelling.</summary>
    /// <param name="key">The key to look for, matched case-insensitively.</param>
    /// <returns>True when the block carries the key.</returns>
    public bool Contains(string key) => Find(key) is not null;

    /// <summary>
    /// Returns the spelling a write should use for this key. Without going through this method a
    /// differently-cased spelling passes the case-insensitive membership test and then lands as a
    /// second key beside the first.
    /// </summary>
    /// <param name="key">The key as the caller gave it.</param>
    /// <param name="declaredSpelling">The declaration's own spelling, used where the block does not
    /// already carry the key. This type knows no vocabulary, so the spelling is passed in rather
    /// than looked up.</param>
    /// <returns>The block's own spelling where the key is present, otherwise
    /// <paramref name="declaredSpelling"/>, or the key as given when that is null.</returns>
    public string Canonicalize(string key, string? declaredSpelling = null) =>
        Find(key) is { } present ? Spelling(present) : declaredSpelling ?? key;

    /// <summary>
    /// Returns what this key holds. Reading the key's value cannot answer this, because a
    /// block-valued key, an absent key, and a key declared with no value all read back the same,
    /// and a scalar set aimed at a key holding a list would replace the list with a string.
    /// </summary>
    /// <param name="key">The key to inspect.</param>
    /// <returns>The key's shape.</returns>
    public FrontmatterShape ShapeOf(string key) => Find(key) switch
    {
        null => FrontmatterShape.Absent,
        var found => root.Children[found] is YamlSequenceNode or YamlMappingNode
            ? FrontmatterShape.Block
            : FrontmatterShape.Scalar,
    };

    /// <summary>
    /// Returns whether this key holds a block list - a sequence of entries - rather than a single
    /// value or a nested mapping.
    /// </summary>
    /// <param name="key">The key to inspect.</param>
    /// <returns>True where the key is present and holds a sequence.</returns>
    /// <remarks>
    /// <see cref="ShapeOf"/> answers <see cref="FrontmatterShape.Block"/> for a nested mapping and
    /// for a sequence alike, and only a sequence holds entries. Anything that resolves, inserts or
    /// removes entries asks this instead. A nested mapping resolves to no entries at all, so a check
    /// written against the shape query alone passes vacuously and lets an entry operation reach a
    /// structure it cannot address.
    /// </remarks>
    public bool HoldsBlockList(string key) =>
        Find(key) is { } found && root.Children[found] is YamlSequenceNode;

    /// <summary>Returns the text under this key.</summary>
    /// <param name="key">The key to read.</param>
    /// <returns>The key's text, or null where the key is absent or holds a block. A key present
    /// with no value reads as the empty string, which is what distinguishes it from an absent
    /// one.</returns>
    public string? Scalar(string key) =>
        Find(key) is { } found && root.Children[found] is YamlScalarNode scalar ? scalar.Value ?? string.Empty : null;

    /// <summary>Returns the scalar items under this key.</summary>
    /// <param name="key">The key to read.</param>
    /// <returns>The scalar items, empty where the key is absent or does not hold a sequence. An
    /// entry that is a mapping has no scalar rendering and is reached through
    /// <see cref="Entries"/> instead.</returns>
    public IReadOnlyList<string> Sequence(string key) =>
        Find(key) is { } found && root.Children[found] is YamlSequenceNode sequence
            ? [.. sequence.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? string.Empty)]
            : [];

    /// <summary>Returns the entries under this key, in order.</summary>
    /// <param name="key">The key to read.</param>
    /// <returns>The entries, empty where the key holds no sequence. Every accessor here returns an
    /// empty collection rather than null, because every caller enumerates the result.</returns>
    public IReadOnlyList<FrontmatterEntry> Entries(string key) =>
        Find(key) is { } found && root.Children[found] is YamlSequenceNode sequence
            ? [.. sequence.Children.Select(node => new FrontmatterEntry(node))]
            : [];

    /// <summary>
    /// Puts a scalar under this key, keeping its existing position where the key is already present
    /// and appending it where it is not. The spelling written is the canonical one, so a
    /// differently-cased key can never land beside the one already in the block.
    /// </summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The text to write under it.</param>
    /// <param name="declaredSpelling">The declaration's own spelling, used when the key is absent.</param>
    public void Set(string key, string value, string? declaredSpelling = null)
    {
        var found = Find(key);
        if (found is not null)
            root.Children[found] = new YamlScalarNode(value);
        else
            root.Children.Add(new YamlScalarNode(Canonicalize(key, declaredSpelling)), new YamlScalarNode(value));
    }

    /// <summary>Removes this key, whatever it holds.</summary>
    /// <param name="key">The key to remove.</param>
    /// <returns>True when the key was there.</returns>
    public bool Remove(string key)
    {
        if (Find(key) is not { } found)
            return false;

        root.Children.Remove(found);
        return true;
    }

    /// <summary>
    /// Inserts an entry into this key's block list at <paramref name="index"/>, appending when that
    /// is the list's length. The key is created holding an empty list where it is absent, which is
    /// how an add creates its key and, through the composer, the block on a note that has none.
    ///
    /// The payload's fields each become a tree value that the emitter escapes, so a caller's value
    /// cannot come out the other side as structure. Whether the payload satisfies the declaration
    /// is checked before an operation reaches here.
    /// </summary>
    /// <param name="key">The block-list key to insert under.</param>
    /// <param name="payload">The entry's fields and values, or a bare scalar.</param>
    /// <param name="index">Where in the list the entry goes, clamped to the list's bounds.</param>
    /// <param name="declaredSpelling">The declaration's own spelling, used when the key is absent.</param>
    /// <exception cref="InvalidOperationException">The key is present and does not hold a block list.</exception>
    internal void InsertEntry(string key, EntryPayload payload, int index, string? declaredSpelling = null)
    {
        var found = Find(key);
        if (found is null)
        {
            found = new YamlScalarNode(Canonicalize(key, declaredSpelling));
            root.Children.Add(found, new YamlSequenceNode());
        }

        if (root.Children[found] is not YamlSequenceNode sequence)
            throw new InvalidOperationException($"\"{key}\" does not hold a block list.");

        sequence.Children.Insert(Math.Clamp(index, 0, sequence.Children.Count), Node(payload));
    }

    /// <summary>
    /// Removes these positions from this key's block list, and the key itself where that empties
    /// the list. An empty tree emits no fence, so a removal that empties the block drops the fence
    /// with it.
    /// </summary>
    /// <param name="key">The block-list key to remove from.</param>
    /// <param name="positions">The 0-based positions to remove. Duplicates and out-of-range values
    /// are ignored.</param>
    internal void RemoveEntriesAt(string key, IReadOnlyList<int> positions)
    {
        if (Find(key) is not { } found || root.Children[found] is not YamlSequenceNode sequence)
            return;

        foreach (var position in positions.Distinct().OrderDescending())
            if (position >= 0 && position < sequence.Children.Count)
                sequence.Children.RemoveAt(position);

        if (sequence.Children.Count == 0)
            root.Children.Remove(found);
    }

    /// <summary>
    /// Returns a tree holding this key's node alone. The outcome diff compares one key from each
    /// side by emitting both through the composer's settings, so a reserialization that changed
    /// only quoting does not read as a change.
    /// </summary>
    /// <param name="key">The key to isolate.</param>
    /// <returns>A tree holding just that key, or an empty tree where the key is absent.</returns>
    internal FrontmatterTree KeyOnly(string key)
    {
        var isolated = new YamlMappingNode();
        if (Find(key) is { } found)
            isolated.Children.Add(found, root.Children[found]);

        return new FrontmatterTree(isolated);
    }

    private static YamlNode Node(EntryPayload payload)
    {
        if (payload is EntryPayload.Scalar scalar)
            return new YamlScalarNode(scalar.Value);

        var mapping = new YamlMappingNode();
        foreach (var field in ((EntryPayload.Mapping)payload).Fields)
            mapping.Children.Add(new YamlScalarNode(field.Name), new YamlScalarNode(field.Value));

        return mapping;
    }

    /// <summary>Returns the key node carrying this spelling, or null where the block has none.
    /// This is the only place key names are compared.</summary>
    /// <param name="key">The key to look for, matched case-insensitively.</param>
    /// <returns>The matching key node, or null.</returns>
    private YamlNode? Find(string key) =>
        root.Children.Keys.FirstOrDefault(k => string.Equals(Spelling(k), key, KeyComparison));

    private static string Spelling(YamlNode key) => key is YamlScalarNode scalar ? scalar.Value ?? string.Empty : key.ToString();
}

/// <summary>
/// Represents one entry in a block list, read tolerantly. An entry is a bare scalar or a mapping of
/// fields, and which it is decides how a caller reads it. Which variant those fields satisfy is the
/// declaration's question, not this type's.
/// </summary>
public sealed class FrontmatterEntry
{
    private readonly YamlNode node;

    /// <summary>Initializes an entry over a YAML node.</summary>
    /// <param name="node">The node the entry reads from.</param>
    internal FrontmatterEntry(YamlNode node) => this.node = node;

    /// <summary>Gets whether the entry is a bare scalar rather than a mapping of fields.</summary>
    public bool IsScalar => node is YamlScalarNode;

    /// <summary>Gets the entry's text where it is a bare scalar, and null where it is not.</summary>
    public string? Value => node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : null;

    /// <summary>Gets the entry's field names, in order, empty where the entry is not a mapping.</summary>
    public IReadOnlyList<string> Fields =>
        node is YamlMappingNode mapping
            ? [.. mapping.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? string.Empty)]
            : [];

    /// <summary>Returns one field's text.</summary>
    /// <param name="name">The field name, matched case-insensitively as key names are.</param>
    /// <returns>The field's text, or null where the entry is not a mapping, the field is not there,
    /// or the field itself holds a block.</returns>
    public string? Field(string name)
    {
        if (node is not YamlMappingNode mapping)
            return null;

        var key = mapping.Children.Keys.OfType<YamlScalarNode>()
            .FirstOrDefault(k => string.Equals(k.Value, name, StringComparison.OrdinalIgnoreCase));

        return key is not null && mapping.Children[key] is YamlScalarNode scalar ? scalar.Value ?? string.Empty : null;
    }
}
