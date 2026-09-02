using System.Text.Json.Nodes;
using TinyWin2.Core;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;
using TinyWin2.Core.Pipeline;

namespace TinyWin2.Cli.Commands;

internal static class InspectHandler {
    public static Task<int> ExecuteAsync(InspectRequest request) {
        var input = request.Input;
        var log = new BuildLog();
        return Cli.RunCancellableAsync("inspect cancelled.", async ct => {
            var resolver = new SourceImageResolver(new ProcessRunner(), log);
            var source = await resolver.ResolveAsync(input, ct);
            try {
                var indexes = await resolver.GetIndexesAsync(source.InstallImagePath, ct);
                if (request.Json) {
                    var root = new JsonObject {
                        ["input"] = input,
                        ["installImage"] = source.InstallImagePath,
                        ["kind"] = source.Kind.ToString().ToLowerInvariant(),
                        ["format"] = source.IsEsd ? "esd" : "wim",
                        ["indexes"] = new JsonArray([.. indexes.Select(i => new JsonObject {
                            ["index"] = i.Index,
                            ["name"] = i.Name,
                            ["description"] = i.Description,
                            ["editionId"] = i.EditionId,
                            ["version"] = i.Version,
                            ["architecture"] = i.Architecture,
                            ["sizeBytes"] = i.SizeBytes
                        })])
                    };
                    Console.WriteLine(root.ToPrettyString());
                    return ExitCodes.Success;
                }

                Console.WriteLine($"input: {input} ({source.Kind.ToString().ToLowerInvariant()})");
                Console.WriteLine($"install image: {source.InstallImagePath} ({(source.IsEsd ? "ESD" : "WIM")})");
                Console.WriteLine();
                Console.WriteLine($"{"idx",-4} {"name",-45} {"edition",-18} {"version",-12} size");
                foreach (var index in indexes) {
                    Console.WriteLine(
                        $"{index.Index,-4} {Cli.Truncate(index.Name, 45),-45} {Cli.Truncate(index.EditionId ?? "-", 18),-18} {Cli.Truncate(index.Version ?? "-", 12),-12} {index.SizeBytes / 1024.0 / 1024:F0} MB");
                }

                return ExitCodes.Success;
            }
            finally {
                await resolver.DismountIsoAsync(source, CancellationToken.None);
            }
        });
    }
}
