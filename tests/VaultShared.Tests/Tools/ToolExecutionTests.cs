using System.Text.Json;
using VaultShared.Auth;
using VaultShared.Configuration;
using VaultShared.Scope;
using VaultShared.Tools;

namespace VaultShared.Tests.Tools;

/// <summary>
/// Tests the shared execute path every tool invocation goes through: the client session check, the
/// op class floor, argument validation, vault resolution and membership, the vault lock, and the
/// access object handed to the tool. Those checks run in that order, and a refusal from an earlier
/// one wins over a refusal from a later one. Several cases here break two of them at once and assert
/// which refusal comes back.
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

    // --- The client session -----------------------------------------------------------------

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

    /// <summary>The session check runs before the floor check. A caller with neither a session nor
    /// the required op class is told the tool is unavailable, rather than being sent to ask for
    /// authority that still would not let the call through.</summary>
    [Fact]
    public void MissingSessionAndMissingFloor_AnswersTheSessionRefusal()
    {
        var declaration = SyntheticTools.Declaration(floor: new Floor.Class(OpClass.Write), requiresClientSession: true);

        var refusal = Refused(declaration, new RecordingTool(), new { }, SyntheticTools.Grant(), session: null);

        Assert.Equal(ToolCodes.ToolUnavailable, refusal.Code);
    }

    // --- The op class floor ------------------------------------------------------------------

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
        // The refusal may name the class the tool requires, which is a property of the tool, but
        // must not reveal which classes the grant holds.
        Assert.DoesNotContain(OpClass.Read, data.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, tool.Invocations);
    }

    /// <summary>A tool declares that it gates itself; there is no list of exempt tool names. A
    /// self-gated tool runs under a grant holding no op classes at all.</summary>
    [Fact]
    public void SelfGatedTool_RunsUnderAGrantHoldingNoOpClasses()
    {
        var tool = new RecordingTool();

        Run(SyntheticTools.Declaration(floor: new Floor.SelfGated()), tool, new { }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(1, tool.Invocations);
    }

    // --- Arguments ---------------------------------------------------------------------------

    [Fact]
    public void ArgumentFault_IsInvalidArgumentCarryingTheArgument()
    {
        var refusal = Refused(SyntheticTools.Declaration(), new RecordingTool(), new { note = "Widget" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        Assert.Equal("note", envelope.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    /// <summary>A binding failure is turned into a coded refusal here, so nothing downstream has to
    /// interpret a serializer message.</summary>
    [Fact]
    public void ArgumentOfTheWrongType_IsInvalidArgumentNamingIt()
    {
        var refusal = Refused(SyntheticTools.Declaration(), new RecordingTool(), new { note = 7 }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        Assert.Equal("note", envelope.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    /// <summary>
    /// A member no argument record declares is refused rather than dropped, so a misspelled argument
    /// name cannot silently turn a guarded call into an unguarded one. The refusal names the member,
    /// and the message says the tool does not take it, which is a different remedy from correcting a
    /// value.
    /// </summary>
    /// <param name="arguments">The argument record type the synthetic tool declares.</param>
    [Theory]
    [InlineData(typeof(PlainArguments))]
    [InlineData(typeof(VaultBearingArguments))]
    public void UndeclaredArgumentMember_IsInvalidArgumentNamingIt(Type arguments)
    {
        var tool = new RecordingTool();

        var refusal = Refused(
            SyntheticTools.Declaration(arguments: arguments), tool,
            new { nte = "Widget.md" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        Assert.Contains("takes no argument named \"nte\"", refusal.Message, StringComparison.Ordinal);
        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        Assert.Equal("nte", envelope.RootElement.GetProperty("data").GetProperty("argument").GetString());
        Assert.Equal(0, tool.Invocations);
    }

    /// <summary>An undeclared member inside a nested argument is named by the path a caller would fix,
    /// rather than by the outer argument, which the tool does take.</summary>
    [Fact]
    public void UndeclaredMemberInsideANestedArgument_IsNamedByItsPath()
    {
        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(EditArguments)), new RecordingTool(),
            new { path = "Note.md", body = new { contnt = "# New\n" } }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        Assert.Equal("body.contnt", envelope.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    /// <summary>An undeclared member inside a list element is named the same way, with the element's
    /// index carried across so the caller can find the operation it belongs to.</summary>
    [Fact]
    public void UndeclaredMemberInsideAListElement_IsNamedByItsPath()
    {
        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(EditArguments)), new RecordingTool(),
            new { path = "Note.md", operations = new[] { new { op = "set", ky = "doc.title" } } },
            SyntheticTools.Grant(), SyntheticTools.Session);

        using var envelope = JsonDocument.Parse(ToolRefusalEnvelope.Json(refusal));
        Assert.Equal("operations[0].ky", envelope.RootElement.GetProperty("data").GetProperty("argument").GetString());
    }

    /// <summary>A value of the wrong shape keeps its own message. The two failures arrive under the
    /// same code and the same payload member, and are told apart by what the message says to do.</summary>
    [Fact]
    public void ArgumentOfTheWrongType_SaysTheShapeIsWrongRatherThanThatTheToolTakesNoSuchArgument()
    {
        var refusal = Refused(SyntheticTools.Declaration(), new RecordingTool(), new { note = 7 }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Contains("does not match the shape", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("takes no argument", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Authorization runs before argument validation so that a caller without authority over a tool
    /// cannot use a malformed call to learn what the tool's arguments are named or what they
    /// reject.
    /// </summary>
    [Fact]
    public void CallerWithoutTheFloor_CannotProbeTheArgumentSurface()
    {
        var declaration = SyntheticTools.Declaration(floor: new Floor.Class(OpClass.Write));

        var refusal = Refused(declaration, new RecordingTool(), new { note = "Widget" }, SyntheticTools.Grant(OpClass.Read), SyntheticTools.Session);

        Assert.Equal(ToolCodes.AuthorityRequired, refusal.Code);
        Assert.DoesNotContain("note", ToolRefusalEnvelope.Json(refusal), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same holds for a caller with no session, whose check runs earlier still.</summary>
    [Fact]
    public void SessionLessCaller_CannotProbeTheArgumentSurfaceOfAFloorGatedTool()
    {
        var declaration = SyntheticTools.Declaration(floor: new Floor.Class(OpClass.Write), requiresClientSession: true);

        var refusal = Refused(declaration, new RecordingTool(), new { note = "Widget" }, SyntheticTools.Grant(OpClass.Write), session: null);

        Assert.Equal(ToolCodes.ToolUnavailable, refusal.Code);
        Assert.DoesNotContain("note", ToolRefusalEnvelope.Json(refusal), StringComparison.OrdinalIgnoreCase);
    }

    // --- Vault resolution and membership ------------------------------------------------------

    /// <summary>Whether a call targets a vault is decided by the arguments type. A record that does
    /// not implement <see cref="IVaultArguments"/> reaches no vault, whatever its members are
    /// named.</summary>
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
    public void ConfiguredButSkippedVault_IsVaultUnavailable()
    {
        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), new RecordingTool(),
            new { vault = "broken" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.VaultUnavailable, refusal.Code);
    }

    /// <summary>Vault lookup runs before the membership check, so a vault that is not configured is
    /// reported as unknown rather than as out of scope.</summary>
    [Fact]
    public void UnknownVaultOutsideTheGrant_AnswersRoutingRatherThanScope()
    {
        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), new RecordingTool(),
            new { vault = "no-such-vault" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.UnknownVault, refusal.Code);
    }

    /// <summary>A vault the grant does not cover is refused as out of scope, and the tool is never
    /// invoked.</summary>
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
        // The refusal must not reveal which vaults the grant does cover.
        Assert.DoesNotContain(SyntheticTools.VaultName, data.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>Argument validation runs before vault resolution, so a malformed argument is refused
    /// before the vault it names is looked up.</summary>
    [Fact]
    public void MalformedArgumentAndUnknownVault_AnswersTheArgumentRefusal()
    {
        var refusal = Refused(
            SyntheticTools.Declaration(arguments: typeof(VaultBearingArguments)), new RecordingTool(),
            new { vault = "no-such-vault", note = "Widget" }, SyntheticTools.Grant(), SyntheticTools.Session);

        Assert.Equal(ToolCodes.InvalidArgument, refusal.Code);
    }

    // --- The vault lock and the access object -------------------------------------------------

    /// <summary>The lock is held for the whole invocation of a mutating tool. The test proves it by
    /// failing to acquire the same mutex from another thread while the tool is running.</summary>
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
        // And released afterwards, or the next call would block instead of running.
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

    /// <summary>The execute path constructs the access object, so a tool only ever holds one it was
    /// handed, and it carries the caller's own grant.</summary>
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

    /// <summary>Reports whether a named mutex can be acquired right now, from a thread that releases
    /// it immediately. It has to be another thread, because a <see cref="Mutex"/> is re-entrant for
    /// the thread that owns it.</summary>
    /// <param name="lockName">The name of the mutex to try.</param>
    /// <returns>True if the mutex was acquired; otherwise false.</returns>
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
