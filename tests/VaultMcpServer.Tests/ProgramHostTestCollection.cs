
namespace VaultMcpServer.Tests;

/// <summary>
/// Groups every test class that boots the real Program through WebApplicationFactory and overrides
/// its config path environment variables.
/// </summary>
/// <remarks>
/// xUnit runs collections in parallel but runs the classes within one collection sequentially.
/// These classes must share a collection because the config path variables are process-global, and
/// a second fixture starting concurrently would overwrite the first's values mid-startup.
/// </remarks>
[CollectionDefinition("Program host tests")]
public sealed class ProgramHostTestCollection;
