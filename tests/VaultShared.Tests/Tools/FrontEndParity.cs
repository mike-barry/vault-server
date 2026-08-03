using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// The parity contract between the two front-ends: for a given refusal, the exact bytes each must
/// put on its own transport, and for a given call, the neutral result each must render.
///
/// <b>It lives here because it is a claim about both front-ends and belongs to neither's suite.</b>
/// Each front-end's own tests assert its emission against these values - the MCP server that the
/// text of an <c>isError</c> result is one of them, the CLI that standard error is - so
/// "one declaration projects into both with the same semantics" is checked at both ends against one
/// statement of it rather than at each end against its own.
///
/// A refusal is raised in one of three vocabularies, and every one of them reaches both transports
/// through <see cref="ToolRefusalEnvelope"/>. That is what these cases pin: not merely that both
/// front-ends produce valid JSON, but that they produce the same JSON, byte for byte, down to field
/// order, to the escaping, and to <c>data</c> being absent rather than empty.
/// </summary>
public static class FrontEndParity
{
    /// <summary>One refusal and the envelope both front-ends owe for it.</summary>
    /// <param name="Name">What the case is, for a failure message.</param>
    /// <param name="Raised">The refusal as the layer raises it, in its own vocabulary.</param>
    /// <param name="Envelope">The bytes both front-ends must emit.</param>
    public sealed record RefusalCase(string Name, Exception Raised, string Envelope);

    /// <summary>
    /// One case per refusal vocabulary that can reach a front-end: a tool's own coded refusal, the
    /// frontmatter surface's, and the mid-operation key assertion's. A fourth vocabulary added to
    /// one front-end's catch and not the other is exactly what this set exists to make visible.
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

    /// <summary>The content tier: text that is the answer itself, carried unchanged by both
    /// front-ends.</summary>
    public static ToolResult Content { get; } = new ToolResult.Content("Projects/Widget/Widget.md\nProjects/Widget/Tasks.md");

    /// <summary>The structured tier, with multi-word members so the shared naming policy is actually
    /// exercised rather than being invisible on single-word ones.</summary>
    public static ToolResult Structured { get; } = new ToolResult.Data(new StructuredAnswer("Projects/Widget/Widget.md", true, ["nav.contents"]));

    /// <summary>What <see cref="Structured"/> serializes to under the shared options - one naming
    /// policy for results and arguments alike, on both transports.</summary>
    public const string StructuredJson =
        "{\"path\":\"Projects/Widget/Widget.md\",\"frontmatter_changed\":true,\"changed_keys\":[\"nav.contents\"]}";

    /// <summary>A stand-in for a mutation result.</summary>
    public sealed record StructuredAnswer(string Path, bool FrontmatterChanged, IReadOnlyList<string> ChangedKeys);
}
