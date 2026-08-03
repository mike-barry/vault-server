using System.Text.Json;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// The six steps every invocation takes, and the ordering guarantee that makes them worth stating
/// as steps: <b>a refusal from an earlier step wins over one from a later</b>. Each case here puts
/// two steps in violation at once and asserts which one answers.
/// </summary>
public sealed class ToolExecutionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vault-tool-execution-").FullName;
    private readonly VaultConfig _config;

    public ToolExecutionTests()
    {
        var skippedRoot = Path.Combine(_root, "gone");
        _config = new VaultConfig
        {
            Vaults = new Dictionary<string, VaultConfig.VaultEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [SyntheticTools.VaultName] = new(_root, PathPolicy.None),
                ["other"] = new(_root, PathPolicy.None),
            },
            DefaultVault = SyntheticTools.VaultName,
            SkippedVaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["broken"] = $"root directory does not exist: {skippedRoot}",
            },
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private ToolRefusal Refused(ToolDeclaration declaration, ITool tool, object arguments, Grant grant, ClientSession? session) =>
        Assert.Throws<ToolRefusal>(() => Run(declaration, tool, arguments, grant, session));

    private ToolResult Run(ToolDeclaration declaration, ITool tool, object arguments, Grant grant, ClientSession? session)
    {
        var registry = new ToolRegistry().Add(declaration, tool);
        return new ToolExecution(_config).Invoke(registry.Find(declaration.Name)!.Value, Args(arguments), grant, session);
    }

    // --- 1. Session ------------------------------------------------------------------------

    [Fact]
    public void SessionRequiringTool_WithNoSession_IsToolUnavailable()
    {
        var declaration = SyntheticTools.Declaration(requiresClientSession: true);
        var tool = new RecordingTool();

        var refusal = Refused(declaration, tool, new { }, SyntheticTools.Grant(), session: null);

        Assert.Equal(ToolCodes.ToolUnavailable, refusal.Code);
        Assert.Equal(0, tool.Invocations);
    }

    [Fact]
    public void SessionRequiringTool_WithASession_Runs()
    {
        var tool = new RecordingTool();

        Run(SyntheticTools.Declaration(requiresClientSession: true), tool, new { }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Same(SyntheticTools.Session, tool.LastCall!.Session);
    }

    /// <summary>Step 1 beats step 2: a caller with neither a session nor the floor is told the thing
    /// this front-end can never supply, rather than being sent to ask for authority that would still
    /// not let the call through.</summary>
    [Fact]
    public void MissingSessionAndMissingFloor_AnswersTheSessionRefusal()
    {
        var declaration = SyntheticTools.Declaration(floor: new Floor.Class(OpClass.Write), requiresClientSession: true);

        var refusal = Refused(declaration, new RecordingTool(), new { }, SyntheticTools.Grant(), session: null);

        Assert.Equal(ToolCodes.ToolUnavailable, refusal.Code);
    }

    // --- 2. The op-class floor ---------------------------------------------------------------

    [Fact]
    public void FloorNotHeld_IsAuthorityRequiredNamingTheRequiredClass()
    {
        var declaration = SyntheticTools.Declaration(floor: new Floor.Class(OpClass.Write));
        var tool = new RecordingTool();

        var refusal = Refused(declaration, tool, new { }, SyntheticTools.Grant(OpClass.Read), SyntheticTools.Session);

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        var data = envelope.RootElement.GetProperty("data");
        Assert.Equal(OpClass.Write, data.GetProperty("required_op_class").GetString());
        // No-oracle: the class the tool requires is a property of the tool; the classes the grant
        // holds are not.
        Assert.DoesNotContain(OpClass.Read, data.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, tool.Invocations);
    }

    /// <summary>The exemption is a declared property, not a name on a list. A self-gated tool runs
    /// under a grant holding nothing at all.</summary>
    [Fact]
    public void SelfGatedTool_RunsUnderAGrantHoldingNoOpClasses()
    {
        var tool = new RecordingTool();

        Run(SyntheticTools.Declaration(floor: new Floor.SelfGated()), tool, new { }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(1, tool.Invocations);
    }

    // --- 3. Arguments -----------------------------------------------------------------------

    [Fact]
    public void ArgumentFault_IsInvalidArgumentCarryingTheArgument()
    {
        var refusal = Refused(SyntheticTools.Declaration(), new RecordingTool(), new { note = "Widget" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        Assert.Equal("note", envelope.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    /// <summary>A binding failure is not a coded refusal until this one site makes it one, so nothing
    /// downstream re-derives it from a serializer message.</summary>
    [Fact]
    public void ArgumentOfTheWrongType_IsInvalidArgumentNamingIt()
    {
        var refusal = Refused(SyntheticTools.Declaration(), new RecordingTool(), new { note = 7 }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        Assert.Equal("note", envelope.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    /// <summary>
    /// The reason authorization precedes argument feedback: a caller holding no authority over a
    /// tool must not be able to use a malformed call to learn what the tool's arguments are called or
    /// what they refuse.
    /// </summary>
    [Fact]
    public void CallerWithoutTheFloor_CannotProbeTheArgumentSurface()
    {
        var declaration = SyntheticTools.Declaration(floor: new Floor.Class(OpClass.Write));

        var refusal = Refused(declaration, new RecordingTool(), new { note = "Widget" }, SyntheticTools.Grant(OpClass.Read), SyntheticTools.Session);

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.DoesNotContain("note", ToolRefusalEnvelope.Json(refusal), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>And the same for a session-less caller, which is one step earlier still.</summary>
    [Fact]
    public void SessionLessCaller_CannotProbeTheArgumentSurfaceOfAFloorGatedTool()
    {
        var declaration = SyntheticTools.Declaration(floor: new Floor.Class(OpClass.Write), requiresClientSession: true);

        var refusal = Refused(declaration, new RecordingTool(), new { note = "Widget" }, SyntheticTools.Grant(OpClass.Write), session: null);

        Assert.Equal(ToolCodes.ToolUnavailable, refusal.Code);
    }

    // --- 4. Vault resolution and membership --------------------------------------------------

    /// <summary>Whether a call targets a vault is a type question. A record that does not implement
    /// <see cref="IVaultArguments"/> reaches no vault at all, however its members are spelled.</summary>
    [Fact]
    public void ArgumentsNotImplementingIVaultArguments_ResolveNoVault()
    {
        var tool = new RecordingTool();

        Run(SyntheticTools.Declaration(), tool, new { }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Null(tool.LastCall!.Vault);
    }

    [Fact]
    public void VaultBearingArguments_ResolveTheDefaultVaultWhenNoneIsNamed()
    {
        var tool = new RecordingTool();

        Run(SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), tool, new { }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(SyntheticTools.VaultName, tool.LastCall!.Vault!.Vault.Name);
    }

    [Fact]
    public void UnknownVault_IsUnknownVault()
    {
        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), new RecordingTool(),
            new { vault = "no-such-vault" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.UnknownVault, refusal.Code);
    }

    [Fact]
    public void ConfiguredButSkippedVault_IsVaultUnavailable()
    {
        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), new RecordingTool(),
            new { vault = "broken" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.VaultUnavailable, refusal.Code);
    }

    /// <summary>Routing precedes authorization: a vault nobody configured is told that, rather than
    /// refused for a scope that could never have admitted it.</summary>
    [Fact]
    public void UnknownVaultOutsideTheGrant_AnswersRoutingRatherThanScope()
    {
        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), new RecordingTool(),
            new { vault = "no-such-vault" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.UnknownVault, refusal.Code);
    }

    /// <summary>The membership check now reaches a compiled tool, which the retained dispatcher's
    /// predicate could not.</summary>
    [Fact]
    public void VaultOutsideTheGrantsMembership_IsOutOfScope()
    {
        var tool = new RecordingTool();

        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), tool,
            new { vault = "other" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.OutOfScope, refusal.Code);
        Assert.Equal(0, tool.Invocations);

        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        var data = envelope.RootElement.GetProperty("data");
        Assert.Equal("other", data.GetProperty("vault").GetString());
        // The denial must not become an enumeration surface for the grant's own vault set.
        Assert.DoesNotContain(SyntheticTools.VaultName, data.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>Step 3 beats step 4: a malformed argument answers before the vault it names is even
    /// looked up.</summary>
    [Fact]
    public void MalformedArgumentAndUnknownVault_AnswersTheArgumentRefusal()
    {
        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), new RecordingTool(),
            new { vault = "no-such-vault", note = "Widget" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
    }

    // --- 5. The lock, and 6. the access object ------------------------------------------------

    /// <summary>The hold spans the whole invocation, so the tool observes the lock as held - proved
    /// by a second acquire from another thread failing while the tool is running.</summary>
    [Fact]
    public void MutatingTool_HoldsTheVaultLockAcrossTheWholeInvocation()
    {
        var vault = new Access.Vault(SyntheticTools.VaultName, _root, PathPolicy.None);
        bool heldDuringInvocation = false;
        var tool = new RecordingTool(_ =>
        {
            heldDuringInvocation = !TryAcquireFromAnotherThread(vault.LockName);
            return new ToolResult.Content("ran");
        });

        Run(SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments), mutates: true), tool, new { }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.True(heldDuringInvocation);
        // And released afterwards, or the next call would deadlock rather than run.
        Assert.True(TryAcquireFromAnotherThread(vault.LockName));
    }

    [Fact]
    public void NonMutatingTool_TakesNoLock()
    {
        var vault = new Access.Vault(SyntheticTools.VaultName, _root, PathPolicy.None);
        bool freeDuringInvocation = false;
        var tool = new RecordingTool(_ =>
        {
            freeDuringInvocation = TryAcquireFromAnotherThread(vault.LockName);
            return new ToolResult.Content("ran");
        });

        Run(SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), tool, new { }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.True(freeDuringInvocation);
    }

    /// <summary>The access object is the execute path's to construct, so no tool holds one it was not
    /// handed - and it carries the caller's own grant.</summary>
    [Fact]
    public void TheAccessObjectIsConstructedFromTheResolvedVaultAndTheCallersGrant()
    {
        var tool = new RecordingTool();
        var grant = SyntheticTools.Grant(OpClass.Read);

        Run(SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), tool, new { }, grant, SyntheticTools.Session);

        var access = tool.LastCall!.Vault;
        Assert.NotNull(access);
        Assert.Equal(SyntheticTools.VaultName, access.Vault.Name);
        Assert.Same(grant, tool.LastCall.Grant);
        access.AssertKey(OpClass.Read);
        Assert.Throws<Access.AuthorityRefusal>(() => access.AssertKey(OpClass.Write));
    }

    /// <summary>Whether the named mutex can be taken right now, from a thread that releases it
    /// immediately. Another thread, because a <see cref="Mutex"/> is re-entrant for its owner and the
    /// question here is whether anyone else could get in.</summary>
    private static bool TryAcquireFromAnotherThread(string lockName)
    {
        bool acquired = false;
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, lockName);
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (acquired)
                mutex.ReleaseMutex();
        });
        thread.Start();
        thread.Join();
        return acquired;
    }
}
