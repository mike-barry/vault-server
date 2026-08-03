namespace VaultCli;

/// <summary>The executable's entry point, and nothing else: everything an invocation does is
/// <see cref="VaultCliApp"/>'s, so a test runs the same path this does.</summary>
internal static class Program
{
    private static int Main(string[] args) => VaultCliApp.Run(args, Console.Out, Console.Error);
}
