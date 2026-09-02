using System.Text.Json.Nodes;
using TinyWin2.Core;
using TinyWin2.Core.Env;

namespace TinyWin2.Cli.Commands;

internal static class DoctorHandler {
    public static int Execute(DoctorRequest request) {
        if (!OperatingSystem.IsWindows()) {
            Console.Error.WriteLine("error: tinywin2 requires Windows (DISM/diskpart/VHDX).");
            return ExitCodes.UnsupportedPlatform;
        }

        var checks = EnvironmentDoctor.Check(request.Workspace);
        var json = request.Json;
        if (json) {
            var root = new JsonObject {
                ["checks"] = new JsonArray([.. checks.Select(check => new JsonObject {
                    ["name"] = check.Name,
                    ["ok"] = check.Ok,
                    ["required"] = check.Required,
                    ["detail"] = check.Detail
                })])
            };
            Console.WriteLine(root.ToPrettyString());
            var failed = checks.Any(c => c is { Required: true, Ok: false });
            return failed ? ExitCodes.Failure : ExitCodes.Success;
        }

        var width = Math.Max("administrator".Length, checks.Max(c => c.Name.Length));
        foreach (var check in checks) {
            var marker = check.Ok ? "OK  " : check.Required ? "FAIL" : "WARN";
            var previous = Console.ForegroundColor;
            Console.ForegroundColor =
                check.Ok ? ConsoleColor.Green : check.Required ? ConsoleColor.Red : ConsoleColor.Yellow;
            Console.Write($"[{marker}] ");
            Console.ForegroundColor = previous;
            Console.WriteLine($"{check.Name.PadRight(width)}  {check.Detail}{(check.Required ? "" : "  (optional)")}");
        }

        var failedRequired = checks.Count(c => c is { Required: true, Ok: false });
        Console.WriteLine(failedRequired == 0
            ? "environment is ready."
            : $"{failedRequired} required check(s) failed.");
        return failedRequired == 0 ? ExitCodes.Success : ExitCodes.Failure;
    }
}
