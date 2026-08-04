using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Holds the shared expectations both front-ends are checked against: for a given refusal, the
/// exact bytes each must write to its own transport, and for a given call, the result each must
/// render.
///
/// This is a claim about both front-ends, so it lives here rather than in either one's test suite.
/// The MCP server's tests compare the text of an <c>isError</c> result against these values and the
/// CLI's tests compare standard error against them, which is what makes the two comparable.
///
/// Refusals are raised by three different layers, each with its own exception type, and all of them
/// reach both transports through <see cref="ToolRefusalEnvelope"/>. The expected strings are
/// compared byte for byte, including field order, escaping, and <c>data</c> being omitted rather
/// than emitted as an empty object.
/// </summary>
public static class FrontEndParity
{
    /// <summary>Represents one refusal and the envelope both front-ends must emit for it.</summary>
    /// <param name="Name">A description of the case, used in failure messages.</param>
    /// <param name="Raised">The exception as the layer that detects the problem throws it.</param>
    /// <param name="Envelope">The exact JSON both front-ends must emit for that exception.</param>
    public sealed record RefusalCase(string Name, Exception Raised, string Envelope);

    /// <summary>
    /// Gets one case for each exception type that can reach a front-end: a tool's own coded
    /// refusal, a frontmatter refusal, and a mid-operation authority refusal. Add a case here when
    /// a new exception type becomes reachable, so that catching it in only one front-end is
    /// caught by these tests.
    /// </summary>
    public static IReadOnlyList<RefusalCase> Refusals { get; } =
    [
        new(
            "the op-class floor, with a payload",
            new ToolRefusal(
                ToolCodes.AuthorityRequired,
                "Grant does not permit content:write operations for tool \"edit\".",
                new { tool = "edit", requiredOpClass = "content:write" }),
            "{\"code\":\"authority_required\",\"message\":\"Grant does not permit content:write operations for tool \\u0022edit\\u0022.\",\"data\":{\"tool\":\"edit\",\"required_op_class\":\"content:write\"}}"),

        new(
            "an out-of-scope path, which carries no payload at all",
            new ToolRefusal(ToolCodes.OutOfScope, "\"Secrets/Ledger.md\" is outside this grant's scope."),
            "{\"code\":\"out_of_scope\",\"message\":\"\\u0022Secrets/Ledger.md\\u0022 is outside this grant\\u0027s scope.\"}"),

        new(
            "a frontmatter refusal, translated and carrying its own coordinates",
            FrontmatterRefusal.At(RefusalReason.UnknownKey, 2, "agent.mode", "No declaration recognizes the key \"agent.mode\"."),
            "{\"code\":\"unknown_key\",\"message\":\"No declaration recognizes the key \\u0022agent.mode\\u0022.\",\"data\":{\"keys\":[\"agent.mode\"],\"operation\":2}}"),

        new(
            "a mid-operation key assertion, translated and carrying nothing",
            new AuthorityRefusal("agentic:write"),
            "{\"code\":\"authority_required\",\"message\":\"This operation requires the \\u0022agentic:write\\u0022 op class.\"}"),
    ];

    /// <summary>Gets a text result, which both front-ends must carry through unchanged.</summary>
    public static ToolResult Content { get; } = new ToolResult.Content("Projects/Widget/Widget.md\nProjects/Widget/Tasks.md");

    /// <summary>Gets a structured result. Its members are deliberately multi-word so that the
    /// shared property naming policy shows up in the serialized output.</summary>
    public static ToolResult Structured { get; } = new ToolResult.Data(new StructuredAnswer("Projects/Widget/Widget.md", true, ["nav.contents"]));

    /// <summary>The JSON <see cref="Structured"/> serializes to under the shared options. The same
    /// naming policy applies to results and arguments on both transports.</summary>
    public const string StructuredJson =
        "{\"path\":\"Projects/Widget/Widget.md\",\"frontmatter_changed\":true,\"changed_keys\":[\"nav.contents\"]}";

    /// <summary>Represents a stand-in for a mutation result, used to exercise serialization.</summary>
    /// <param name="Path">The vault-relative path the result is about.</param>
    /// <param name="FrontmatterChanged">Whether the frontmatter changed.</param>
    /// <param name="ChangedKeys">The frontmatter keys that changed.</param>
    public sealed record StructuredAnswer(string Path, bool FrontmatterChanged, IReadOnlyList<string> ChangedKeys);
}
