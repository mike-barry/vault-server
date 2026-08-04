namespace VaultCli;

/// <summary>Provides the executable's entry point. All of the work is in <see cref="VaultCliApp"/>,
/// so a test exercises the same path the shipped executable takes.</summary>
internal static class Program
{
    /// <summary>Runs one invocation against the console streams.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>One of <see cref="ExitCodes"/>.</returns>
    private static int Main(string[] args) => VaultCliApp.Run(args, Console.Out, Console.Error);
}
