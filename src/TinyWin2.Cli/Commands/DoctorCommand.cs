using System.Text.Json.Nodes;
using TinyWin2.Core;
using TinyWin2.Core.Env;

namespace TinyWin2.Cli.Commands;

internal static class DoctorCommand {
    public static int Run(Dictionary<string, string> options) {
        if (!OperatingSystem.IsWindows()) {
            Console.Error.WriteLine("error: tinywin2 requires Windows (DISM/diskpart/VHDX).");
            return 3;
        }
        var json = options.ContainsKey("json");
        var checks = EnvironmentDoctor.Check(options.GetValueOrDefault("out"));
        if (json) {
            var root = new JsonObject { ["checks"] = Json.ToNode(checks) };
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
