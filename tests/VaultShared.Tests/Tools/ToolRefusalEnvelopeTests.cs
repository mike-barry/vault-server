using System.Text.Json;
using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// The envelope's wire form and the one table that translates refusals raised in another vocabulary
/// into it. Both front-ends emit the envelope, so a change here is a change to what every caller
/// sees, whichever transport it is on.
/// </summary>
public sealed class ToolRefusalEnvelopeTests
{
    // --- The wire form ----------------------------------------------------------------------

    /// <summary>Byte-exact, and deliberately so: this is the form the MCP front-end already put on
    /// the wire, and a caller tests for <c>data</c>'s presence rather than its nullness.</summary>
    [Fact]
    public void Json_WithoutData_IsCompactAndOmitsTheDataFieldEntirely()
    {
        var json = ToolRefusalEnvelope.Json(ToolCodes.AuthorityRequired, "Grant does not permit content:write operations.");

        Assert.Equal(
            """{"code":"authority_required","message":"Grant does not permit content:write operations."}""",
            json);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void Json_WithData_EmitsCodeThenMessageThenData()
    {
        var json = ToolRefusalEnvelope.Json(ToolCodes.OutOfScope, "Not scoped.", new { tool = "read", vault = "test" });

        Assert.Equal("""{"code":"out_of_scope","message":"Not scoped.","data":{"tool":"read","vault":"test"}}""", json);
        // Field order is part of the parity, not incidental - assert it independently of the literal
        // above so a serializer change that reorders is caught by name.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["code", "message", "data"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    /// <summary>
    /// One naming policy governs the whole wire, in both directions. A payload member authored in
    /// PascalCase or camelCase reaches a caller in <c>snake_case</c>, exactly as an argument does, so
    /// nothing translates between a sent name and a returned one.
    /// </summary>
    [Fact]
    public void Json_PayloadNames_AreSnakeCase()
    {
        var json = ToolRefusalEnvelope.Json(ToolCodes.AuthorityRequired, "Denied.", new { requiredOpClass = "content:write", limitPerMinute = 5 });

        Assert.Contains("\"required_op_class\":\"content:write\"", json);
        Assert.Contains("\"limit_per_minute\":5", json);
    }

    [Fact]
    public void Json_OfARefusal_CarriesItsCodeMessageAndData()
    {
        var json = ToolRefusalEnvelope.Json(new ToolRefusal(ToolCodes.LockTimeout, "Not acquired.", new { timeout = 30.0 }));

        Assert.Equal("""{"code":"lock_timeout","message":"Not acquired.","data":{"timeout":30}}""", json);
    }

    // --- The RefusalReason table --------------------------------------------------------------

    /// <summary>
    /// Every member of the frontmatter surface's closed reason set has a wire code. The table is the
    /// single site that decides one, so a reason added without a code has to fail here rather than at
    /// the first call that happens to raise it.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryReason))]
    public void Translate_EveryRefusalReason_HasACode(RefusalReason reason)
    {
        var refusal = ToolRefusalEnvelope.Translate(FrontmatterRefusal.Of(reason, "Refused."));

        Assert.False(string.IsNullOrEmpty(refusal.Code));
        Assert.Equal("Refused.", refusal.Message);
    }

    public static TheoryData<RefusalReason> EveryReason()
    {
        var data = new TheoryData<RefusalReason>();
        foreach (var reason in Enum.GetValues<RefusalReason>())
            data.Add(reason);
        return data;
    }

    /// <summary>
    /// The distinctions the table must not collapse. Each pair has a different remedy, so a shared
    /// code would send a caller to the wrong fix.
    /// </summary>
    [Theory]
    // Whose text it is: fix what was sent, or fix the note.
    [InlineData(RefusalReason.Unparsable, ToolCodes.FrontmatterUnparsable)]
    [InlineData(RefusalReason.NotAMapping, ToolCodes.FrontmatterUnparsable)]
    [InlineData(RefusalReason.SuppliedBlockInvalid, ToolCodes.InvalidArgument)]
    // The tooling does not own it, which no grant changes - as against authority, which elevation does.
    [InlineData(RefusalReason.PathExcluded, ToolCodes.PathExcluded)]
    [InlineData(RefusalReason.AuthorityDenied, ToolCodes.AuthorityRequired)]
    // Nothing matched against too many matched.
    [InlineData(RefusalReason.UnresolvedIdentity, ToolCodes.EntryNotFound)]
    [InlineData(RefusalReason.AmbiguousIdentity, ToolCodes.EntryAmbiguous)]
    // An undeclared key against a declared key holding the wrong shape or a refused value.
    [InlineData(RefusalReason.UnknownKey, ToolCodes.UnknownKey)]
    [InlineData(RefusalReason.ShapeMismatch, ToolCodes.ShapeMismatch)]
    [InlineData(RefusalReason.ValueRefused, ToolCodes.InvalidValue)]
    [InlineData(RefusalReason.ControlCharacter, ToolCodes.InvalidValue)]
    [InlineData(RefusalReason.FenceConflict, ToolCodes.FenceConflict)]
    // The fail-closed seam, which refuses frontmatter writes alone.
    [InlineData(RefusalReason.RegistryUnavailable, ToolCodes.FrontmatterUnavailable)]
    public void Translate_MapsEachReasonToItsCode(RefusalReason reason, string expected)
    {
        Assert.Equal(expected, ToolRefusalEnvelope.Translate(FrontmatterRefusal.Of(reason, "Refused.")).Code);
    }

    /// <summary>The payload is the refusal's own coordinates - the key it locates and the operation
    /// that owns it, both of them values the caller supplied.</summary>
    [Fact]
    public void Translate_CarriesTheKeyAndOperationTheRefusalLocates()
    {
        var refusal = ToolRefusalEnvelope.Translate(FrontmatterRefusal.At(RefusalReason.UnknownKey, operation: 2, key: "status", "No declaration recognizes \"status\"."));

        using var doc = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(["status"], data.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(2, data.GetProperty("operation").GetInt32());
    }

    /// <summary>A refusal locating neither carries no payload at all, rather than an object of
    /// nulls.</summary>
    [Fact]
    public void Translate_ARefusalLocatingNothing_CarriesNoPayload()
    {
        var refusal = ToolRefusalEnvelope.Translate(FrontmatterRefusal.Of(RefusalReason.FenceConflict, "A write would destroy a fence."));

        Assert.Null(refusal.Data);
        Assert.DoesNotContain("data", ToolRefusalEnvelope.Json(refusal));
    }

    /// <summary>The mid-operation key assertion's denial carries no payload: the class it required is
    /// in the message, and the remedy is elevation rather than a corrected call.</summary>
    [Fact]
    public void Translate_AnAuthorityRefusal_IsAuthorityRequiredWithNoPayload()
    {
        var refusal = ToolRefusalEnvelope.Translate(new AuthorityRefusal(OpClass.Write));

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.Null(refusal.Data);
        Assert.Contains(OpClass.Write, refusal.Message);
    }
}
