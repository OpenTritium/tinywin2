using System.Text.Json.Nodes;
using TinyWin2.Core.Env;

namespace TinyWin2.Cli.Commands;

internal static class DoctorCommand {
    public static int Run(Dictionary<string, string> options) {
        var json = options.ContainsKey("json");
        var checks = EnvironmentDoctor.Check(options.TryGetValue("out", out var outDir) ? outDir : null);
        if (json) {
            var root = new JsonObject { ["checks"] = TinyWin2.Core.Json.ToNode(checks) };
            Console.WriteLine(root.ToJsonString(JsonSerializerOptions));
            var failed = checks.Any(c => c.Required && !c.Ok);
            return failed ? 1 : 0;
        }
        var width = Math.Max("administrator".Length, checks.Max(c => c.Name.Length));
        foreach (var check in checks) {
            var marker = check.Ok ? "OK  " : check.Required ? "FAIL" : "WARN";
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = check.Ok ? ConsoleColor.Green : check.Required ? ConsoleColor.Red : ConsoleColor.Yellow;
            Console.Write($"[{marker}] ");
            Console.ForegroundColor = previous;
            Console.WriteLine($"{check.Name.PadRight(width)}  {check.Detail}{(check.Required ? "" : "  (optional)")}");
        }
        var failedRequired = checks.Count(c => c.Required && !c.Ok);
        Console.WriteLine(failedRequired == 0
            ? "environment is ready."
            : $"{failedRequired} required check(s) failed.");
        return failedRequired == 0 ? 0 : 1;
    }

    internal static readonly System.Text.Json.JsonSerializerOptions JsonSerializerOptions = new() {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
