using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Layers;

/// <summary>Picks the Hyper-V cmdlet backend when available, diskpart otherwise.</summary>
public static class LayerBackendFactory {
    public static ILayerBackend Create(IProcessRunner runner) =>
        HyperVhdBackend.IsAvailable()
            ? new HyperVhdBackend(runner)
            : new DiskPartVhdBackend(runner);
}

public enum LayerStatus {
    Pending,
    Committed,
    Discarded,
    Failed,
    Merged,
}

/// <summary>Persistent record of one layer in the chain (layer 0 is the base).</summary>
public sealed record LayerRecord {
    public int Index { get; init; }
    public string? StepId { get; init; }
    public string? Title { get; init; }
    public string VhdxFileName { get; init; } = "";
    public string? VhdxPath { get; init; }
    public LayerStatus Status { get; init; } = LayerStatus.Pending;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedUtc { get; init; }
    public JsonObject? BoundArgs { get; init; }
    public JsonArray? ExecResults { get; init; }
    public string? Error { get; init; }
    public long SizeBytes => VhdxPath is { } path && File.Exists(path) ? new FileInfo(path).Length : 0;

    public JsonObject ToJson() => new() {
        ["index"] = Index,
        ["stepId"] = StepId,
        ["title"] = Title,
        ["vhdx"] = VhdxFileName,
        ["status"] = Status.ToString().ToLowerInvariant(),
        ["startedUtc"] = StartedUtc.ToString("O"),
        ["endedUtc"] = EndedUtc?.ToString("O"),
        ["boundArgs"] = BoundArgs?.DeepClone(),
        ["execResults"] = ExecResults?.DeepClone(),
        ["error"] = Error,
    };
}

/// <summary>An attached layer being worked on: mount root + bookkeeping.</summary>
public sealed class LayerSession {
    public required LayerRecord Record { get; init; }
    public required string VhdxPath { get; init; }
    public required string MountPath { get; init; }
}

/// <summary>
/// The VHDX overlay chain: base.vhdx + L001..Ln.vhdx differencing layers with a
/// persistent manifest (<c>layers.json</c>). Layer atomicity = a failed layer's VHDX is
/// deleted, leaving the image byte-identical to before the step started.
/// </summary>
public sealed class VhdLayerStack(
    string workDirectory,
    ILayerBackend backend,
    BuildLog log) {
    private const string BaseFileName = "base.vhdx";
    private const string ManifestFileName = "layers.json";
    private const int ConsolidateThreshold = 30;

    private readonly object _gate = new();
    private readonly List<LayerRecord> _records = [];

    public string WorkDirectory { get; } = workDirectory;
    public string BaseVhdxPath => Path.Combine(WorkDirectory, BaseFileName);
    private string ManifestPath => Path.Combine(WorkDirectory, ManifestFileName);

    /// <summary>Current chain leaf (deepest committed layer whose VHDX still exists; base after merges).</summary>
    public string LeafVhdxPath {
        get {
            lock (_gate) {
                foreach (var record in _records.Where(r => r.Status is LayerStatus.Committed or LayerStatus.Merged).Reverse()) {
                    if (record.VhdxFileName == BaseFileName) {
                        return BaseVhdxPath;
                    }
                    if (record.VhdxPath is not null && File.Exists(record.VhdxPath)) {
                        return record.VhdxPath;
                    }
                    // Merged-away diffs fall through: their content lives in the base now.
                }
                return BaseVhdxPath;
            }
        }
    }

    /// <summary>Total layers committed into the image (base + diffs, merged-away ones
    /// included) — a reporting count; the live chain depth resets on consolidation.</summary>
    public int CommittedDepth {
        get {
            lock (_gate) {
                return _records.Count(r => r.Status is LayerStatus.Committed or LayerStatus.Merged);
            }
        }
    }

    /// <summary>Committed diff layers still on disk; drops back to 0 after consolidation.</summary>
    private int ChainDepth {
        get {
            lock (_gate) {
                return _records.Count(r => r.Status == LayerStatus.Committed && r.VhdxFileName != BaseFileName);
            }
        }
    }

    public IReadOnlyList<LayerRecord> Records {
        get {
            lock (_gate) {
                return _records.ToArray();
            }
        }
    }

    // ---- lifecycle ---------------------------------------------------------

    public static VhdLayerStack Load(string workDirectory, ILayerBackend backend, BuildLog log) {
        var stack = new VhdLayerStack(workDirectory, backend, log);
        if (File.Exists(stack.ManifestPath)) {
            try {
                var root = JsonNode.Parse(File.ReadAllText(stack.ManifestPath))?["layers"] as JsonArray ?? [];
                foreach (var node in root.OfType<JsonObject>()) {
                    var record = new LayerRecord {
                        Index = node["index"]!.GetValue<int>(),
                        StepId = node["stepId"]?.GetValue<string>(),
                        Title = node["title"]?.GetValue<string>(),
                        VhdxFileName = node["vhdx"]!.GetValue<string>(),
                        Status = Enum.Parse<LayerStatus>(node["status"]!.GetValue<string>(), ignoreCase: true),
                        StartedUtc = DateTimeOffset.Parse(node["startedUtc"]!.GetValue<string>()),
                        EndedUtc = node["endedUtc"] is { } e ? DateTimeOffset.Parse(e.GetValue<string>()) : null,
                        BoundArgs = node["boundArgs"] as JsonObject,
                        ExecResults = node["execResults"] as JsonArray,
                        Error = node["error"]?.GetValue<string>(),
                        VhdxPath = Path.Combine(workDirectory, node["vhdx"]!.GetValue<string>()),
                    };
                    lock (stack._gate) {
                        stack._records.Add(record);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException) {
                // A manifest we cannot read is not resumable: fail with the recovery hint
                // instead of a raw parser stack trace.
                throw new IOException(
                    $"layer manifest '{stack.ManifestPath}' is corrupt ({ex.Message}); delete the workspace and rebuild.",
                    ex);
            }
        }
        return stack;
    }

    /// <summary>Persists the manifest atomically (staging file + move): a crash mid-write
    /// must not corrupt the resume state.</summary>
    public void Save() {
        lock (_gate) {
            Directory.CreateDirectory(WorkDirectory);
            var layers = new JsonArray();
            foreach (var record in _records) {
                layers.Add(record.ToJson());
            }
            var root = new JsonObject { ["layers"] = layers };
            var staging = ManifestPath + ".tmp";
            File.WriteAllText(staging, root.ToPrettyString());
            File.Move(staging, ManifestPath, overwrite: true);
        }
    }

    /// <summary>Creates the base layer VHDX; no-op when one already exists (resumable).</summary>
    public async Task EnsureBaseAsync(long maximumMb, string volumeLabel, CancellationToken ct) {
        if (File.Exists(BaseVhdxPath)) {
            log.Info($"reusing existing base layer {BaseFileName}");
            return;
        }
        Directory.CreateDirectory(WorkDirectory);
        await backend.CreateBaseAsync(BaseVhdxPath, maximumMb, volumeLabel, ct);
        lock (_gate) {
            _records.Add(new LayerRecord {
                Index = 0,
                StepId = null,
                Title = "base image (applied from source index)",
                VhdxFileName = BaseFileName,
                Status = LayerStatus.Committed,
                VhdxPath = BaseVhdxPath,
            });
        }
        Save();
    }

    public async Task ApplyImageToBaseAsync(Func<string, CancellationToken, Task> applyAsync, CancellationToken ct) {
        var letter = await backend.AttachAsync(BaseVhdxPath, ct);
        try {
            await applyAsync($"{letter}:\\", ct);
        }
        finally {
            await backend.DetachAsync(BaseVhdxPath, ct);
        }
    }

    /// <summary>Creates + attaches a fresh differencing layer for one plan step.</summary>
    public async Task<LayerSession> BeginLayerAsync(string stepId, string title, JsonObject? boundArgs, CancellationToken ct) {
        var index = 1 + Math.Max(0, Records.Count > 0 ? Records.Max(r => r.Index) : 0);
        var fileName = $"L{index:D3}.vhdx";
        var vhdxPath = Path.Combine(WorkDirectory, fileName);
        var parent = LeafVhdxPath;
        log.Info($"creating layer {index:000} '{title}' on {Path.GetFileName(parent)}", layerIndex: index);
        await backend.CreateDiffAsync(vhdxPath, parent, ct);
        char letter;
        try {
            letter = await backend.AttachAsync(vhdxPath, ct);
        }
        catch {
            TryDelete(vhdxPath, strict: false);
            throw;
        }
        var record = new LayerRecord {
            Index = index,
            StepId = stepId,
            Title = title,
            VhdxFileName = fileName,
            Status = LayerStatus.Pending,
            BoundArgs = boundArgs,
            VhdxPath = vhdxPath,
        };
        lock (_gate) {
            _records.Add(record);
        }
        Save();
        return new LayerSession { Record = record, VhdxPath = vhdxPath, MountPath = $"{letter}:\\" };
    }

    /// <summary>Detaches and keeps the layer (plan succeeded).</summary>
    public async Task CommitLayerAsync(LayerSession session, JsonArray? execResults, CancellationToken ct) {
        await backend.DetachAsync(session.VhdxPath, ct);
        lock (_gate) {
            UpdateRecord(session.Record.Index, record => record with {
                Status = LayerStatus.Committed,
                EndedUtc = DateTimeOffset.UtcNow,
                ExecResults = execResults ?? record.ExecResults,
            });
        }
        Save();
        log.Info($"layer {session.Record.Index:000} committed ({new FileInfo(session.VhdxPath).Length / 1024.0 / 1024:F1} MB)",
            layerIndex: session.Record.Index);
        if (ChainDepth > ConsolidateThreshold) {
            await ConsolidateAsync(ct);
        }
    }

    /// <summary>Detaches and deletes the layer — the image state equals pre-step (atomic rollback).</summary>
    public async Task DiscardLayerAsync(LayerSession session, string? error, CancellationToken ct) {
        try {
            await backend.DetachAsync(session.VhdxPath, ct);
        }
        catch (Exception ex) {
            log.Warn($"detach failed while discarding layer {session.Record.Index:000}: {ex.Message}");
        }
        TryDelete(session.VhdxPath, strict: false);
        lock (_gate) {
            UpdateRecord(session.Record.Index, record => record with {
                Status = LayerStatus.Discarded,
                EndedUtc = DateTimeOffset.UtcNow,
                Error = error,
            });
        }
        Save();
        log.Warn($"layer {session.Record.Index:000} discarded; image state unchanged", layerIndex: session.Record.Index);
    }

    /// <summary>Merges every committed layer into the base and clears the diff files (chain-depth guard).</summary>
    public async Task ConsolidateAsync(CancellationToken ct) {
        // Merged-away diffs are excluded: their files are gone and their content lives in
        // the base — counting them would inflate the merge depth on a second pass.
        var diffs = Records.Where(r => r.Status == LayerStatus.Committed
                                       && r.VhdxFileName != BaseFileName).ToList();
        if (diffs.Count == 0) {
            return;
        }
        var depth = diffs.Count;
        log.Info($"consolidating {depth} layers into the base (chain-depth guard)");
        var leaf = diffs[^1];
        await backend.MergeAsync(leaf.VhdxPath!, depth, ct);
        foreach (var diff in diffs) {
            TryDelete(diff.VhdxPath!, strict: true);
            lock (_gate) {
                UpdateRecord(diff.Index, record => record with {
                    Status = LayerStatus.Merged,
                    EndedUtc = DateTimeOffset.UtcNow,
                });
            }
        }
        Save();
    }

    /// <summary>Consolidates the chain and copies the merged base to <paramref name="targetPath"/>
    /// (a boot-testable VHDX artifact).</summary>
    public async Task<string> ExportMergedVhdxAsync(string targetPath, CancellationToken ct) {
        await ConsolidateAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(BaseVhdxPath, targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>Replaces one record in place (caller must hold <see cref="_gate"/>).</summary>
    private void UpdateRecord(int index, Func<LayerRecord, LayerRecord> update) {
        var position = _records.FindIndex(r => r.Index == index);
        _records[position] = update(_records[position]);
    }

    /// <summary>The VHDX to capture output from when rolling back to a given layer index.</summary>
    public string VhdxForLayer(int index) {
        lock (_gate) {
            if (index == 0) {
                return BaseVhdxPath;
            }
            var record = _records.LastOrDefault(r => r.Index == index && r.Status is LayerStatus.Committed or LayerStatus.Merged)
                         ?? throw new ArgumentException($"layer {index:000} is not committed");
            return record.VhdxPath is not null && File.Exists(record.VhdxPath)
                ? record.VhdxPath
                : BaseVhdxPath; // merged-away layer: its content is in the base
        }
    }

    /// <summary>Deletes a layer file. Strict (consolidation): failure aborts while the manifest
    /// still matches disk. Otherwise a locked file is only disk-space debt — layer indexes are
    /// monotonic, so the name is never reused — and must not mask the caller's real error.</summary>
    private void TryDelete(string path, bool strict) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch (Exception ex) {
            if (strict) {
                throw new IOException($"failed to delete layer file '{path}': {ex.Message}", ex);
            }
            log.Warn($"could not delete layer file '{path}' ({ex.Message}); leaving it behind");
        }
    }
}
