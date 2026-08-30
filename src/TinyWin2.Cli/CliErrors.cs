using System.Text.Json.Nodes;
using TinyWin2.Core;
using TinyWin2.Core.Env;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Native;
using TinyWin2.Core.Pipeline;
using TinyWin2.Core.Plans;

namespace TinyWin2.Cli;

/// <summary>
///     Maps failures to the stable error contract: an exit code (see <see cref="ExitCodes" />),
///     a kebab-case error code, and when the caller asked for JSON (any --json / --json-events
///     option, or TINYWIN2_ERRORS=json) a structured object on stderr.
/// </summary>
internal static class CliErrors {
    /// <summary>Writes the failure and returns the process exit code.</summary>
    public static int Write(Exception ex, bool json) {
        var (code, exit) = Classify(ex);
        WriteErrorObject(code, ex.Message, Hint(code), json);
        return exit;
    }

    /// <summary>Writes the failure without choosing an exit code (handlers with custom flows).</summary>
    public static void Emit(Exception ex, bool json) {
        var (code, _) = Classify(ex);
        WriteErrorObject(code, ex.Message, Hint(code), json);
    }

    private static void WriteErrorObject(string code, string message, string? hint, bool json) {
        if (json) {
            Console.Error.WriteLine(new JsonObject {
                ["error"] = new JsonObject {
                    ["code"] = code,
                    ["message"] = message,
                    ["hint"] = hint
                }
            }.ToCompactString());
        }
        else {
            Console.Error.WriteLine($"error: {message}");
            if (hint is not null) {
                Console.Error.WriteLine($"hint: {hint}");
            }

            if (Environment.GetEnvironmentVariable("TINYWIN2_DEBUG") is "1" or "true") {
                Console.Error.WriteLine("debug: rerun details follow; unset TINYWIN2_DEBUG to silence.");
            }
        }
    }

    private static (string Code, int Exit) Classify(Exception ex) => ex switch {
        OperationCanceledException => ("canceled", ExitCodes.Canceled),
        PlatformNotSupportedException => ("unsupported-platform", ExitCodes.UnsupportedPlatform),
        PlanValidationException => ("plan-validation-failed", ExitCodes.Failure),
        PlanResolutionException => ("plan-resolution-failed", ExitCodes.Failure),
        ParameterBindingException => ("parameter-binding-failed", ExitCodes.Failure),
        BuildStepFailedException => ("step-failed", ExitCodes.Failure),
        EnvironmentCheckFailedException => ("environment-check-failed", ExitCodes.Failure),
        ProcessRunnerException => ("native-tool-failed", ExitCodes.Failure),
        ExecException => ("operation-failed", ExitCodes.Failure),
        OutputExistsException => ("output-exists", ExitCodes.Failure),
        WorkspaceConflictException => ("workspace-conflict", ExitCodes.Failure),
        ArgumentException => ("invalid-argument", ExitCodes.Failure),
        FileNotFoundException or DirectoryNotFoundException => ("not-found", ExitCodes.Failure),
        IOException => ("io-failed", ExitCodes.Failure),
        InvalidOperationException => ("invalid-operation", ExitCodes.Failure),
        _ => ("internal", ExitCodes.Failure)
    };

    private static string? Hint(string code) => code switch {
        "step-failed" => "rerun the same build command with --resume to replay from the last completed step",
        "output-exists" => "pass --overwrite to replace the existing artifact",
        "workspace-conflict" => "choose a new workspace, or pass --resume when continuing an earlier build",
        "plan-resolution-failed" => "run `tinywin2 plan list` to browse valid plan ids",
        "environment-check-failed" => "run `tinywin2 doctor --workspace <dir>` for the failing checks",
        "native-tool-failed" => "run `tinywin2 doctor` to check tool availability",
        "invalid-argument" => "run with --help to see the accepted options",
        "internal" => "set TINYWIN2_DEBUG=1 and rerun for a stack trace",
        _ => null
    };
}
