using System.Text.Json.Nodes;
using TinyWin2.Core.Native;
using TinyWin2.Core.Pipeline;

namespace TinyWin2.Cli.Commands;

internal static class InspectCommand {
    public static async Task<int> RunAsync(List<string> args) {
        var options = Program.ParseOptions(args);
        if (args.Count == 0 || args[0].StartsWith("--")) {
            Console.Error.WriteLine("usage: tinywin2 inspect <iso|folder> [--json]");
            return 2;
        }
        var source = args[0];
        var log = new Core.Logging.BuildLog();
        var resolver = new SourceImageResolver(new ProcessRunner(), log);
        var media = await resolver.ResolveAsync(source, CancellationToken.None);
        var indexes = await resolver.GetIndexesAsync(media.InstallImagePath, CancellationToken.None);
        await resolver.DismountIsoAsync(media, CancellationToken.None);
        if (options.ContainsKey("json")) {
            var root = new JsonObject {
                ["source"] = source,
                ["installImage"] = media.InstallImagePath,
                ["format"] = media.IsEsd ? "esd" : "wim",
                ["indexes"] = new JsonArray(indexes.Select(i => (JsonNode)new JsonObject {
                    ["index"] = i.Index,
                    ["name"] = i.Name,
                    ["description"] = i.Description,
                    ["editionId"] = i.EditionId,
                    ["version"] = i.Version,
                    ["architecture"] = i.Architecture,
                    ["sizeBytes"] = i.SizeBytes,
                }).ToArray()),
            };
            Console.WriteLine(root.ToJsonString(DoctorCommand.JsonSerializerOptions));
            return 0;
        }
        Console.WriteLine($"source: {source}");
        Console.WriteLine($"install image: {media.InstallImagePath} ({(media.IsEsd ? "ESD" : "WIM")})");
        Console.WriteLine();
        Console.WriteLine($"{"idx",-4} {"name",-45} {"edition",-18} {"version",-12} size");
        foreach (var index in indexes) {
            Console.WriteLine($"{index.Index,-4} {Cli.Truncate(index.Name, 45),-45} {Cli.Truncate(index.EditionId ?? "-", 18),-18} {Cli.Truncate(index.Version ?? "-", 12),-12} {index.SizeBytes / 1024.0 / 1024:F0} MB");
        }
        return 0;
    }
}
