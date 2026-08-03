using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace VaultShared.Frontmatter;

/// <summary>What a key holds, as a question a value read cannot answer.</summary>
public enum FrontmatterShape
{
    /// <summary>The key is not in the block.</summary>
    Absent,

    /// <summary>
    /// The key holds a single value, or holds none at all. A valueless key counts as scalar because
    /// there is no list under it to lose; the validator reports it separately as an empty block.
    /// </summary>
    Scalar,

    /// <summary>The key holds a sequence or a nested mapping.</summary>
    Block,
}

/// <summary>What came of parsing a block's inner text.</summary>
public enum TreeParse
{
    /// <summary>A mapping, and the tree is it.</summary>
    Parsed,

    /// <summary>The text is not YAML. A reader treats this as no block; the write path refuses it.</summary>
    Unparsable,

    /// <summary>The text parsed to something other than a mapping - a bare scalar, or a sequence.</summary>
    NotAMapping,
}

/// <summary>
/// A parsed frontmatter block, over YamlDotNet's representation model - the mutable node tree, never
/// its object serializer (<c>Spec-Vault-Access-Design.md:Fixed Constraints</c>). Only the node layer
/// preserves key order and per-node quoting style, which the preservation invariants require.
///
/// The wrapper exists so no caller holds a raw node. It owns canonical key lookup, the three-answer
/// shape query, and the reads that tolerate a malformed block, so no second place decides how a key
/// is matched or what a value's shape is.
///
/// Nothing here coerces a value. A date-shaped scalar is the text that was written, because the node
/// layer carries a scalar's text and its style rather than a typed value.
/// </summary>
public sealed class FrontmatterTree
{
    /// <summary>
    /// Key membership is case-insensitive throughout: lookup, canonicalization, shape, and the reads.
    /// Two spellings of one key are one key, matching how the rest of the layer compares names.
    /// </summary>
    private const StringComparison KeyComparison = StringComparison.OrdinalIgnoreCase;

    private readonly YamlMappingNode root;

    private FrontmatterTree(YamlMappingNode root) => this.root = root;

    /// <summary>The node the composer emits. Internal, so no caller outside this namespace holds one.</summary>
    internal YamlMappingNode Root => root;

    /// <summary>A block with no keys. What a note with no frontmatter parses to, and what a removal empties to.</summary>
    public static FrontmatterTree Empty() => new(new YamlMappingNode());

    /// <summary>
    /// Parse a block's inner text. The two failure answers are distinct because the write path
    /// refuses them for distinct reasons - text that is not YAML, and text that is YAML but not a
    /// mapping - and <paramref name="tree"/> is an empty tree in both, so no caller holds a null.
    /// </summary>
    public static TreeParse TryParse(string innerText, out FrontmatterTree tree)
    {
        tree = Empty();

        YamlStream stream = new();
        try
        {
            // Parsed with a terminator, because the split hands over the block's text without the
            // final inner line's one. A block scalar reads its trailing newline from that terminator,
            // so parsing the text as given would turn `desc: |` into `desc: |-` on the way back out -
            // a byte the emitter moved for no reason anybody asked for. Restoring it here makes the
            // parse and the composer, which always terminates the block, inverses of each other.
            stream.Load(new StringReader(innerText.EndsWith('\n') ? innerText : innerText + "\n"));
        }
        catch (YamlException)
        {
            return TreeParse.Unparsable;
        }

        // An empty fence pair is a block with empty frontmatter: nothing here emits one, but one
        // found on disk reads and edits normally.
        if (stream.Documents.Count == 0)
            return TreeParse.Parsed;

        if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
            return TreeParse.NotAMapping;

        tree = new FrontmatterTree(mapping);
        return TreeParse.Parsed;
    }

    /// <summary>
    /// The tolerant read: a whole note's content to its block, where a block that will not parse
    /// reads as no block. The read accessors report on notes rather than gating them and run over
    /// every file in the vault, so one malformed note must not fail a whole-vault pass. The write
    /// path refuses that same note through <see cref="TryParse"/>, because writing into a block
    /// nobody could parse means guessing.
    /// </summary>
    public static FrontmatterTree Read(string content)
    {
        var split = NoteSplit.Of(content);
        if (!split.HasBlock)
            return Empty();

        TryParse(split.InnerText, out var tree);
        return tree;
    }

    /// <summary>Whether the block holds no keys. An empty tree emits no fence.</summary>
    public bool IsEmpty => root.Children.Count == 0;

    /// <summary>Every key, in the order the block carries them.</summary>
    public IReadOnlyList<string> Keys => [.. root.Children.Keys.Select(Spelling)];

    /// <summary>Whether the block carries this key, under any spelling.</summary>
    public bool Contains(string key) => Find(key) is not null;

    /// <summary>
    /// The spelling a write should use for this key: the tree's own where the key is already there,
    /// and <paramref name="declaredSpelling"/> where it is not. Without this one seam a
    /// differently-cased spelling passes the case-insensitive membership test and then lands as a
    /// second key beside the first.
    ///
    /// The declaration is what supplies the second half, and it is passed in rather than reached for
    /// - this type knows no vocabulary. A caller with no declared spelling to offer gets the key back
    /// as it was given.
    /// </summary>
    public string Canonicalize(string key, string? declaredSpelling = null) =>
        Find(key) is { } present ? Spelling(present) : declaredSpelling ?? key;

    /// <summary>
    /// What this key holds. Reading a value cannot answer this: a block-valued field, an absent
    /// field, and a field declared with no value all read back the same, and a flat scalar operation
    /// aimed at a field holding a list would replace the list with a string.
    /// </summary>
    public FrontmatterShape ShapeOf(string key) => Find(key) switch
    {
        null => FrontmatterShape.Absent,
        var found => root.Children[found] is YamlSequenceNode or YamlMappingNode
            ? FrontmatterShape.Block
            : FrontmatterShape.Scalar,
    };

    /// <summary>
    /// The text under this key, or null where the key is absent or holds a block. A valueless key
    /// reads as the empty string, which is what distinguishes it from an absent one.
    /// </summary>
    public string? Scalar(string key) =>
        Find(key) is { } found && root.Children[found] is YamlScalarNode scalar ? scalar.Value ?? string.Empty : null;

    /// <summary>
    /// The scalar items under this key, empty where the key is absent, is not a sequence, or holds a
    /// sequence of anything else. An entry that is a mapping has no scalar rendering and is reached
    /// through <see cref="Entries"/> instead.
    /// </summary>
    public IReadOnlyList<string> Sequence(string key) =>
        Find(key) is { } found && root.Children[found] is YamlSequenceNode sequence
            ? [.. sequence.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? string.Empty)]
            : [];

    /// <summary>
    /// The entries under this key, in order, empty where the key holds no sequence. Every accessor
    /// returning a collection returns an empty one rather than an absent value: every caller
    /// enumerates the result, and an absent-value return pushes a null check into all of them.
    /// </summary>
    public IReadOnlyList<FrontmatterEntry> Entries(string key) =>
        Find(key) is { } found && root.Children[found] is YamlSequenceNode sequence
            ? [.. sequence.Children.Select(node => new FrontmatterEntry(node))]
            : [];

    /// <summary>
    /// Put a scalar under this key, at its existing position where it is already there and appended
    /// where it is not. The spelling written is the canonical one, so a differently-cased key can
    /// never land beside the one already in the block.
    /// </summary>
    public void Set(string key, string value, string? declaredSpelling = null)
    {
        var found = Find(key);
        if (found is not null)
            root.Children[found] = new YamlScalarNode(value);
        else
            root.Children.Add(new YamlScalarNode(Canonicalize(key, declaredSpelling)), new YamlScalarNode(value));
    }

    /// <summary>Drop this key whatever it holds. Answers whether it was there.</summary>
    public bool Remove(string key)
    {
        if (Find(key) is not { } found)
            return false;

        root.Children.Remove(found);
        return true;
    }

    /// <summary>The key node carrying this spelling, or null. The one place case-insensitivity lives.</summary>
    private YamlNode? Find(string key) =>
        root.Children.Keys.FirstOrDefault(k => string.Equals(Spelling(k), key, KeyComparison));

    private static string Spelling(YamlNode key) => key is YamlScalarNode scalar ? scalar.Value ?? string.Empty : key.ToString();
}

/// <summary>
/// One entry in a block list, read tolerantly. An entry is a bare scalar or a mapping of fields, and
/// which it is decides how a caller reads it; the declaration decides which variant those fields
/// satisfy, which is not this type's question.
/// </summary>
public sealed class FrontmatterEntry
{
    private readonly YamlNode node;

    internal FrontmatterEntry(YamlNode node) => this.node = node;

    /// <summary>Whether the entry is a bare scalar rather than a mapping of fields.</summary>
    public bool IsScalar => node is YamlScalarNode;

    /// <summary>The entry's text where it is a bare scalar, and null where it is not.</summary>
    public string? Value => node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : null;

    /// <summary>The entry's field names, in order, empty where the entry is not a mapping.</summary>
    public IReadOnlyList<string> Fields =>
        node is YamlMappingNode mapping
            ? [.. mapping.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? string.Empty)]
            : [];

    /// <summary>
    /// One field's text, or null where the entry is not a mapping, the field is not there, or the
    /// field itself holds a block. Matched case-insensitively, as key membership is.
    /// </summary>
    public string? Field(string name)
    {
        if (node is not YamlMappingNode mapping)
            return null;

        var key = mapping.Children.Keys.OfType<YamlScalarNode>()
            .FirstOrDefault(k => string.Equals(k.Value, name, StringComparison.OrdinalIgnoreCase));

        return key is not null && mapping.Children[key] is YamlScalarNode scalar ? scalar.Value ?? string.Empty : null;
    }
}
