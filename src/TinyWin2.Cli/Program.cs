using TinyWin2.Cli.Commands;

namespace TinyWin2.Cli;

internal static class Program
{
    private const string Version = "2.0.0-alpha1";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "doctor" => DoctorCommand.Run(ParseOptions(args[1..])),
                "version" or "--version" => RunVersion(),
                "help" or "--help" or "-h" => RunHelp(),
                _ => RunUnknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    /// <summary>Parses <c>--key value</c> and <c>--flag</c> style options (flag → "true").</summary>
    internal static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = "true";
            }
        }
        return options;
    }

    private static int RunVersion()
    {
        Console.WriteLine($"tinywin2 {Version}");
        return 0;
    }

    private static int RunHelp() => RunUnknown("help");

    private static int RunUnknown(string command)
    {
        PrintUsage();
        if (command != "help" && command != "--help" && command != "-h")
        {
            Console.Error.WriteLine($"unknown command: {command}");
            return 2;
        }
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            tinywin2 {Version} — layered Windows image slimming (VHDX differencing chains)

            usage: tinywin2 <command> [options]

            commands:
              doctor                 check environment (admin, tools, disk space)
              version                print version
              help                   show this help

            (build / inspect / plan / profile / layer commands land in M4)
            """);
    }
}
