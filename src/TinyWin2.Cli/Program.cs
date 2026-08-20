using TinyWin2.Cli.Commands;

namespace TinyWin2.Cli;

internal static class Program {
    private const string Version = "2.0.0-alpha1";

    private static async Task<int> Main(string[] args) {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0) {
            PrintUsage();
            return 0;
        }
        try {
            var command = args[0].ToLowerInvariant();
            var rest = args[1..].ToList();
            return command switch {
                "doctor" => DoctorCommand.Run(ToSingleOptions(ParseOptions(rest))),
                "inspect" => await InspectCommand.RunAsync(rest),
                "plan" => PlanCommand.Run(rest),
                "profile" => ProfileCommand.Run(rest),
                "build" => await BuildCommand.RunAsync(rest),
                "preview" => await PreviewCommand.RunAsync(rest),
                "layer" => await LayerCommand.RunAsync(rest),
                "version" or "--version" => RunVersion(),
                "help" or "--help" or "-h" => RunHelp(),
                _ => RunUnknown(command),
            };
        }
        catch (Exception ex) {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    /// <summary>Parses <c>--key value</c> / <c>-k value</c> / flags; repeated keys accumulate.</summary>
    internal static Dictionary<string, List<string>> ParseOptions(List<string> args) {
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++) {
            var arg = args[i];
            var isLong = arg.StartsWith("--", StringComparison.Ordinal);
            var isShort = !isLong && arg.Length == 2 && arg[0] == '-' && char.IsLetter(arg[1]);
            if (!isLong && !isShort) {
                continue;
            }
            var key = isLong ? arg[2..] : arg[1..].ToString();
            var value = "true";
            if (i + 1 < args.Count) {
                var next = args[i + 1];
                var nextIsOption = next.StartsWith("--", StringComparison.Ordinal)
                    || (next.Length == 2 && next[0] == '-' && char.IsLetter(next[1]));
                if (!nextIsOption) {
                    value = next;
                    i++;
                }
            }
            if (!options.TryGetValue(key, out var values)) {
                options[key] = values = [];
            }
            values.Add(value);
        }
        return options;
    }

    /// <summary>Old single-value view for commands that only need flags.</summary>
    internal static Dictionary<string, string> ToSingleOptions(Dictionary<string, List<string>> options) =>
        options.ToDictionary(kv => kv.Key, kv => kv.Value[^1], StringComparer.OrdinalIgnoreCase);

    private static int RunVersion() {
        Console.WriteLine($"tinywin2 {Version}");
        return 0;
    }

    private static int RunHelp() => RunUnknown("help");

    private static int RunUnknown(string command) {
        PrintUsage();
        if (command != "help" && command != "--help" && command != "-h") {
            Console.Error.WriteLine($"unknown command: {command}");
            return 2;
        }
        return 0;
    }

    private static void PrintUsage() {
        Console.WriteLine($"""
            tinywin2 {Version} — layered Windows image slimming (VHDX differencing chains)

            usage: tinywin2 <command> [options]

            commands:
              doctor                 check environment (admin, tools, disk space)
              inspect <iso|folder>   list image indexes
              plan list|show         explore the plan catalog
              profile list|show|export|import
              build                  run a layered slimming build
              preview                apply base layer + report what each plan WOULD change
              layer list|diff|extract|rollback-to   post-mortem the layer chain

            build options:
              -s <iso|folder> -i <index>            source and image index
              --plan <id> [--plan ...]              select plans
              --set planId.arg=value [--set ...]    set plan arguments
              --profile <file>                      load a selection profile
              --out wim|esd|iso|iso+vhdx            output mode (default iso)
              -o <dir>                              output root (default ./out)
              --granularity group|plan              one layer per group (default) or per plan
              [--fast] [--continue-on-error] [--keep-layers] [--dry-run]
              [--oscdimg <path>] [--json-events]

            examples:
              tinywin2 inspect D:\iso\server2025.iso
              tinywin2 build -s D:\iso\server2025.iso -i 1 --plan appx.xbox --plan service.workstation --set service.workstation.startMode=manual
              tinywin2 layer diff out/work/<buildId> 2 3 --deep
            """);
    }
}
