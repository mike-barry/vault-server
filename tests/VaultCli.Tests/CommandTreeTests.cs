using System.CommandLine;
using VaultCli;
using VaultShared.Tools;

namespace VaultCli.Tests;

/// <summary>
/// The command tree is generated from the registry and from the argument records the declarations
/// name. Nothing here asserts a hand-written list of subcommands or options: what is under test is
/// that the generation covers whatever is registered, so a tool added to the registry needs no line
/// in this front-end and no line in these tests.
/// </summary>
public sealed class CommandTreeTests(CliFixture fixture) : IClassFixture<CliFixture>
{
    /// <summary>A declaration that needs a caller whose authority can be rebound - what
    /// <c>mint_token</c> and <c>request_elevation</c> are, in a shape this suite can register. The
    /// CLI's own registry holds neither, so the filter has nothing to act on without one.</summary>
    private sealed class SessionTool : ITool
    {
        public static readonly ToolDeclaration Declaration = new()
        {
            Name = "needs_session",
            Description = "Mutates per-process authority state keyed to a live caller's token.",
            Arguments = typeof(NoArguments),
            OpClassFloor = new Floor.SelfGated(),
            RequiresClientSession = true,
        };

        public ToolResult Invoke(ToolCall call) => throw new InvalidOperationException("Not reachable from a one-shot process.");
    }

    [Fact]
    public void EveryProjectedDeclaration_IsASubcommandNamedForIt()
    {
        var host = Host();

        var root = ToolCommandTree.Build(host.Tools, host, TextWriter.Null, TextWriter.Null);

        Assert.Equal(
            host.Tools.Registrations.Select(r => r.Declaration.Name).Order().ToArray(),
            root.Subcommands.Select(c => c.Name).Order().ToArray());
    }

    [Fact]
    public void ADeclarationRequiringAClientSession_IsNotListed()
    {
        var host = Host();
        var tools = new ToolRegistry()
            .Add(GlobTool.Declaration, new GlobTool())
            .Add(SessionTool.Declaration, new SessionTool());

        var root = ToolCommandTree.Build(tools, host, TextWriter.Null, TextWriter.Null);

        Assert.Equal(["glob"], root.Subcommands.Select(c => c.Name).ToArray());
    }

    /// <summary>
    /// The filter above is cosmetic, and this is what makes that safe: the shared execute path
    /// refuses such a call whatever route reached it, and <c>--args</c> reaches a tool by name
    /// without the tree.
    /// </summary>
    [Fact]
    public void ADeclarationRequiringAClientSession_IsRefusedByTheExecutePath()
    {
        var host = Host();

        var refusal = Assert.Throws<ToolRefusal>(() => host.Execution.Invoke(
            new ToolRegistry.Registration(SessionTool.Declaration, new SessionTool()),
            new Dictionary<string, System.Text.Json.JsonElement>(),
            host.Grant(CliFixture.AdminGrant),
            session: null));

        Assert.Equal(ToolCodes.ToolUnavailable, refusal.Code);
    }

    /// <summary>
    /// Both spellings come from the one declared name. A single-word argument spells the same either
    /// way and registers one alias, not a duplicate.
    /// </summary>
    [Theory]
    [InlineData("read", "--with-content-hash", "--with_content_hash")]
    [InlineData("edit", "--expected-content-hash", "--expected_content_hash")]
    [InlineData("grep", "--path-glob", "--path_glob")]
    [InlineData("create", "--git-keep", "--git_keep")]
    public void AMultiWordArgument_IsPresentedInKebabCaseWithTheWireSpellingAsAnAlias(string tool, string kebab, string snake)
    {
        var option = Option(tool, kebab);

        Assert.Equal(kebab, option.Name);
        Assert.Equal([snake], option.Aliases.ToArray());
    }

    [Fact]
    public void ASingleWordArgument_RegistersNoAlias() => Assert.Empty(Option("read", "--path").Aliases);

    /// <summary>
    /// One option per member of the argument record, and no member without one. The expected set is
    /// read off the same contract the binder reads, so this asserts coverage rather than restating
    /// the record.
    /// </summary>
    [Fact]
    public void EverySubcommand_CarriesOneOptionPerMemberOfItsArgumentRecord()
    {
        var host = Host();
        var root = ToolCommandTree.Build(host.Tools, host, TextWriter.Null, TextWriter.Null);

        foreach (var registration in host.Tools.Registrations)
        {
            var command = root.Subcommands.Single(c => c.Name == registration.Declaration.Name);
            var expected = NativeToolArguments.Options
                .GetTypeInfo(registration.Declaration.Arguments)
                .Properties
                .Select(p => "--" + ToolOptions.Kebab(p.Name))
                .Order()
                .ToArray();

            // The globals are recursive and reach every command; what is generated is the rest.
            var generated = command.Options
                .Where(o => o.Name is not ("--grant" or "--output" or "--args" or "--help"))
                .Select(o => o.Name)
                .Order()
                .ToArray();

            Assert.Equal(expected, generated);
        }
    }

    /// <summary>An argument record with no members - <c>health_check</c>'s - generates no options
    /// and is still a subcommand.</summary>
    [Fact]
    public void ATakingNoArguments_IsStillASubcommand()
    {
        var run = fixture.Run(["health_check"]);

        Assert.Equal(ExitCodes.Success, run.Exit);
    }

    private Option Option(string tool, string name)
    {
        var host = Host();
        var root = ToolCommandTree.Build(host.Tools, host, TextWriter.Null, TextWriter.Null);
        return root.Subcommands.Single(c => c.Name == tool).Options.Single(o => o.Name == name);
    }

    private CliHost Host() => CliHost.Load(CliSettings.Load(fixture.SettingsPath), TextWriter.Null);
}
