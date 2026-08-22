using System.CommandLine;
using System.Text;

namespace TinyWin2.Cli;

internal static class Program {
    private static async Task<int> Main(string[] args) {
        Console.OutputEncoding = Encoding.UTF8;
        var root = CommandLine.CreateRootCommand();
        var configuration = new InvocationConfiguration {
            EnableDefaultExceptionHandler = false
        };

        try {
            return await root.Parse(args).InvokeAsync(configuration);
        }
        catch (Exception ex) {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }
}
