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

        var parseResult = root.Parse(args);
        try {
            return await parseResult.InvokeAsync(configuration);
        }
        catch (Exception ex) {
            var jsonErrors = Environment.GetEnvironmentVariable("TINYWIN2_ERRORS") is "json"
                             || parseResult.Tokens.Any(t => t.Value is "--json" or "--json-events");
            return CliErrors.Write(ex, jsonErrors);
        }
    }
}
