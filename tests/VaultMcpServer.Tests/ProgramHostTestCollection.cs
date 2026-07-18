namespace VaultMcpServer.Tests;

/// <summary>
/// Groups every test class that boots the real Program via WebApplicationFactory and
/// overrides its config path env vars (VaultsConfigPath, GrantsConfigPath,
/// ToolsDirectory). xUnit runs collections in parallel with each other but test classes
/// within one collection sequentially - these must share a collection, since the env vars
/// are process-global and a concurrent second fixture would stomp the first's values
/// mid-startup.
/// </summary>
[CollectionDefinition("Program host tests")]
public sealed class ProgramHostTestCollection;
