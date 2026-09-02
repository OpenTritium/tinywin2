using System.Text.Json;
using System.Text.Json.Nodes;

namespace TinyWin2.Core.Pipeline;

/// <summary>
///     Small, persistent progress record for the single-layer fast path. It records only
///     which prefix was completed; the VHDX itself is rebuilt from the source on resume.
/// </summary>
internal sealed class LayerlessCheckpoint {
    private const int CurrentSchemaVersion = 1;

    private LayerlessCheckpoint(
        string sourceFingerprint,
        int sourceIndex,
        IReadOnlyList<LayerlessCheckpointStep> steps,
        int completedCount) {
        SourceFingerprint = sourceFingerprint;
        SourceIndex = sourceIndex;
        Steps = [.. steps];
        CompletedCount = completedCount;
    }

    public string SourceFingerprint { get; }
    public int SourceIndex { get; }
    public List<LayerlessCheckpointStep> Steps { get; }
    public int CompletedCount { get; set; }

    public static LayerlessCheckpoint Create(
        string sourceFingerprint,
        int sourceIndex,
        IReadOnlyList<(string StepId, string Fingerprint)> steps) =>
        new(sourceFingerprint, sourceIndex,
            [.. steps.Select(step => new LayerlessCheckpointStep(step.StepId, step.Fingerprint))], 0);

    public static LayerlessCheckpoint Load(string path) {
        try {
            var parsed = JsonNode.Parse(File.ReadAllText(path));
            if (parsed is not JsonObject root) {
                throw new InvalidDataException("checkpoint root must be an object");
            }

            var schemaVersion = root["schemaVersion"]?.GetValue<int>()
                                ?? throw new InvalidDataException("checkpoint schemaVersion is missing");
            if (schemaVersion != CurrentSchemaVersion) {
                throw new InvalidDataException(
                    $"unsupported checkpoint schema version {schemaVersion} (expected {CurrentSchemaVersion})");
            }

            var sourceFingerprint = root["sourceFingerprint"]?.GetValue<string>();
            var sourceIndex = root["sourceIndex"]?.GetValue<int>() ?? 0;
            var completedCount = root["completedCount"]?.GetValue<int>() ?? -1;
            if (string.IsNullOrWhiteSpace(sourceFingerprint) || sourceIndex < 1 || completedCount < 0) {
                throw new InvalidDataException("checkpoint source or completedCount is invalid");
            }

            if (root["steps"] is not JsonArray steps) {
                throw new InvalidDataException("checkpoint steps array is missing");
            }

            var records = new List<LayerlessCheckpointStep>(steps.Count);
            for (var index = 0; index < steps.Count; index++) {
                if (steps[index] is not JsonObject step) {
                    throw new InvalidDataException($"checkpoint step {index} is not an object");
                }

                var id = step["stepId"]?.GetValue<string>();
                var fingerprint = step["fingerprint"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fingerprint)) {
                    throw new InvalidDataException($"checkpoint step {index} is incomplete");
                }

                records.Add(new(id, fingerprint));
            }

            return completedCount > records.Count
                ? throw new InvalidDataException("checkpoint completedCount exceeds the step count")
                : new(sourceFingerprint, sourceIndex, records, completedCount);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException
                                       or FormatException or InvalidDataException or OverflowException) {
            throw new IOException(
                $"layerless checkpoint '{path}' is corrupt ({ex.Message}); start a clean build.", ex);
        }
    }

    public void Save(string path) {
        var root = new JsonObject {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["sourceFingerprint"] = SourceFingerprint,
            ["sourceIndex"] = SourceIndex,
            ["completedCount"] = CompletedCount,
            ["steps"] = new JsonArray(Steps.Select(step => (JsonNode)step.ToJson()).ToArray())
        };
        var temporary = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(temporary, root.ToPrettyString());
        File.Move(temporary, path, true);
    }
}

internal sealed record LayerlessCheckpointStep(string StepId, string Fingerprint) {
    public JsonObject ToJson() => new() {
        ["stepId"] = StepId,
        ["fingerprint"] = Fingerprint
    };
}
