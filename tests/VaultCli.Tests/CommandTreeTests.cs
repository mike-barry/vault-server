using System.CommandLine;
using VaultCli;
using VaultShared.Tools;

namespace VaultCli.Tests;

/// <summary>
/// Verifies that the command tree is generated from the tool registry and from the argument records
/// the declarations name. No case asserts a hand-written list of subcommands or options; each
/// asserts that generation covers whatever is registered.
/// </summary>
public sealed class CommandTreeTests(CliFixture fixture) : IClassFixture<CliFixture>
{
    /// <summary>A stand-in tool declaring <see cref="ToolDeclaration.RequiresClientSession"/>, in
    /// the shape of <c>mint_token</c> and <c>request_elevation</c>. The CLI's registry holds neither
    /// of those, so without this the session filter would have nothing to act on.</summary>
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

        // Compared in order, not as sets. The registry's order is the order a front-end lists its
        // tools in, and the tree is the only thing that carries that order into the CLI's help.
        Assert.Equal(
            host.Tools.Registrations.Select(r => r.Declaration.Name).ToArray(),
            root.Subcommands.Select(c => c.Name).ToArray());
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
    /// Leaving such a tool out of the tree is only presentation. The shared execute path refuses
    /// the call whatever route reached it.
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
    /// Both spellings are derived from the single declared name. A single-word argument spells the
    /// same either way, so it registers no alias rather than a duplicate of its own name.
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
    /// Each subcommand carries exactly one option per member of its argument record. The expected
    /// set is read from the same type information the binder uses, so this asserts coverage rather
    /// than restating the record.
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
                .ToArray();

            // The globals are recursive and reach every command; what is generated is the rest.
            // Compared in order, not as sets, because the options are presented in the record's own
            // member order and that is the order --help lists them in.
            var generated = command.Options
                .Where(o => o.Name is not ("--grant" or "--output" or "--args" or "--help"))
                .Select(o => o.Name)
                .ToArray();

            Assert.Equal(expected, generated);
        }
    }

    /// <summary>An argument record with no members, such as <c>health_check</c>'s, generates no
    /// options and is still a subcommand.</summary>
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
