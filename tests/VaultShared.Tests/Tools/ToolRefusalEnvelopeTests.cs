using System.Text.Json;
using VaultShared.Access;
using VaultShared.Frontmatter;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the refusal envelope's wire form and the table that translates refusals raised by other
/// layers into it. Both front-ends emit this envelope, so a change here changes what every caller
/// sees on either transport.
/// </summary>
public sealed class ToolRefusalEnvelopeTests
{
    // --- The wire form ----------------------------------------------------------------------

    /// <summary>The expected JSON is compared byte for byte. A refusal with no payload omits the
    /// <c>data</c> field, because a caller tests whether it is present rather than whether it is
    /// null.</summary>
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
        // Field order is part of the contract. Assert it separately from the literal above so a
        // serializer change that reorders the fields fails with a clear message.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["code", "message", "data"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    /// <summary>
    /// One naming policy applies in both directions. A payload member written in PascalCase or
    /// camelCase reaches a caller in <c>snake_case</c>, the same way an argument does, so a caller
    /// never has to translate between the names it sends and the names it receives.
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

    // --- Translating a RefusalReason to a code ------------------------------------------------

    /// <summary>
    /// Every member of <see cref="RefusalReason"/> maps to a wire code. Adding a reason without a
    /// code fails here rather than at the first call that happens to raise it.
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
    /// The distinctions the mapping must not collapse. Each group below has a different remedy, so
    /// sharing a code across it would send a caller to the wrong fix.
    /// </summary>
    [Theory]
    // Whose text is at fault: the note on disk, or what the caller sent.
    [InlineData(RefusalReason.Unparsable, ToolCodes.FrontmatterUnparsable)]
    [InlineData(RefusalReason.NotAMapping, ToolCodes.FrontmatterUnparsable)]
    [InlineData(RefusalReason.SuppliedBlockInvalid, ToolCodes.InvalidArgument)]
    // A path no grant can reach, against authority a wider grant would supply.
    [InlineData(RefusalReason.PathExcluded, ToolCodes.PathExcluded)]
    [InlineData(RefusalReason.AuthorityDenied, ToolCodes.AuthorityRequired)]
    // Nothing matched, against too many matched.
    [InlineData(RefusalReason.UnresolvedIdentity, ToolCodes.EntryNotFound)]
    [InlineData(RefusalReason.AmbiguousIdentity, ToolCodes.EntryAmbiguous)]
    // An undeclared key, against a declared key given the wrong shape or a refused value.
    [InlineData(RefusalReason.UnknownKey, ToolCodes.UnknownKey)]
    [InlineData(RefusalReason.ShapeMismatch, ToolCodes.ShapeMismatch)]
    [InlineData(RefusalReason.ValueRefused, ToolCodes.InvalidValue)]
    [InlineData(RefusalReason.ControlCharacter, ToolCodes.InvalidValue)]
    [InlineData(RefusalReason.FenceConflict, ToolCodes.FenceConflict)]
    // A declaration that did not load, which refuses frontmatter writes only.
    [InlineData(RefusalReason.RegistryUnavailable, ToolCodes.FrontmatterUnavailable)]
    public void Translate_MapsEachReasonToItsCode(RefusalReason reason, string expected)
    {
        Assert.Equal(expected, ToolRefusalEnvelope.Translate(FrontmatterRefusal.Of(reason, "Refused.")).Code);
    }

    /// <summary>The payload carries the key the refusal is about and the index of the operation that
    /// named it, both values the caller supplied.</summary>
    [Fact]
    public void Translate_CarriesTheKeyAndOperationTheRefusalLocates()
    {
        var refusal = ToolRefusalEnvelope.Translate(FrontmatterRefusal.At(RefusalReason.UnknownKey, operation: 2, key: "status", "No declaration recognizes \"status\"."));

        using var doc = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(["status"], data.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(2, data.GetProperty("operation").GetInt32());
    }

    /// <summary>A refusal that names neither a key nor an operation carries no payload at all,
    /// rather than an object of nulls.</summary>
    [Fact]
    public void Translate_ARefusalLocatingNothing_CarriesNoPayload()
    {
        var refusal = ToolRefusalEnvelope.Translate(FrontmatterRefusal.Of(RefusalReason.FenceConflict, "A write would destroy a fence."));

        Assert.Null(refusal.Data);
        Assert.DoesNotContain("data", ToolRefusalEnvelope.Json(refusal));
    }

    /// <summary>An authority refusal raised mid-operation carries no payload. The op class it needed
    /// is in the message, and the remedy is a wider grant rather than a corrected call.</summary>
    [Fact]
    public void Translate_AnAuthorityRefusal_IsAuthorityRequiredWithNoPayload()
    {
        var refusal = ToolRefusalEnvelope.Translate(new AuthorityRefusal(OpClass.Write));

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.Null(refusal.Data);
        Assert.Contains(OpClass.Write, refusal.Message);
    }
}
