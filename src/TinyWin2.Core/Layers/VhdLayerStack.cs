using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Layers;

/// <summary>Picks the Hyper-V cmdlet backend when available, diskpart otherwise.</summary>
public static class LayerBackendFactory {
    public static ILayerBackend Create(IProcessRunner runner) =>
        HyperVhdBackend.IsAvailable(runner)
            ? new HyperVhdBackend(runner)
            : new DiskPartVhdBackend(runner);
}

public enum LayerStatus {
    Pending,
    Committed,
    Discarded,
    Merged
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
    public JsonObject? OperationResult { get; init; }

    /// <summary>XXH3 fingerprint of the step's exec specs and assets.</summary>
    public string? StepFingerprint { get; init; }

    public string? Error { get; init; }
    public long SizeBytes => VhdxPath != null && File.Exists(VhdxPath) ? new FileInfo(VhdxPath).Length : 0;

    public JsonObject ToJson() => new() {
        ["index"] = Index,
        ["stepId"] = StepId,
        ["title"] = Title,
        ["vhdx"] = VhdxFileName,
        ["status"] = Status.ToString().ToLowerInvariant(),
        ["startedUtc"] = StartedUtc.ToString("O"),
        ["endedUtc"] = EndedUtc?.ToString("O"),
        ["operationResult"] = OperationResult?.DeepClone(),
        ["stepFingerprint"] = StepFingerprint,
        ["error"] = Error
    };
}

/// <summary>An attached layer being worked on: mount root + bookkeeping.</summary>
public sealed class LayerSession {
    public required LayerRecord Record { get; init; }
    public required string VhdxPath { get; init; }
    public required string MountPath { get; init; }
}

/// <summary>
///     The VHDX overlay chain: base.vhdx + L001..Ln.vhdx differencing layers with a
///     persistent manifest (<c>layers.json</c>). Layer atomicity = a failed layer's VHDX is
///     deleted, leaving the image byte-identical to before the step started. Consolidation
///     (merging diffs into the base) happens at artifact export; mid-build only when the
///     backend's chain-depth safety limit demands it.
/// </summary>
public sealed class VhdLayerStack(
    string workDirectory,
    ILayerBackend backend,
    BuildLog log) {
    private const string BaseFileName = "base.vhdx";
    private const string ManifestFileName = "layers.json";
    private readonly Lock _gate = new();
    private readonly List<LayerRecord> _records = [];
    private bool _baseReady;
    private bool _consolidationPending;
    private int _nextIndex = 1;
    private string? _sourceFingerprint;
    private int? _sourceIndex;
    private string WorkDirectory { get; } = Path.GetFullPath(workDirectory);
    public string BaseVhdxPath => Path.Combine(WorkDirectory, BaseFileName);
    private string ManifestPath => Path.Combine(WorkDirectory, ManifestFileName);

    /// <summary>True only after the source image was applied and the base was detached successfully.</summary>
    public bool BaseReady {
        get {
            lock (_gate) {
                return _baseReady;
            }
        }
    }

    /// <summary>True when a process may have crashed during an offline chain merge.</summary>
    public bool ConsolidationPending {
        get {
            lock (_gate) {
                return _consolidationPending;
            }
        }
    }

    /// <summary>Current chain leaf (deepest committed layer whose VHDX still exists; base after merges).</summary>
    public string LeafVhdxPath {
        get {
            lock (_gate) {
                foreach (var record in _records.Where(r => r.Status == LayerStatus.Committed).Reverse()) {
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

    /// <summary>
    ///     Total layers committed into the image (base + diffs, merged-away ones
    ///     included) — a reporting count; the live chain depth resets on consolidation.
    /// </summary>
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
                return [.. _records];
            }
        }
    }

    // ---- lifecycle ---------------------------------------------------------

    public static VhdLayerStack Load(string workDirectory, ILayerBackend backend, BuildLog log) {
        var stack = new VhdLayerStack(workDirectory, backend, log);
        if (!File.Exists(stack.ManifestPath)) {
            return stack;
        }

        try {
            var parsed = JsonNode.Parse(File.ReadAllText(stack.ManifestPath));
            if (parsed is not JsonObject root || root["layers"] is not JsonArray layers) {
                throw new InvalidDataException("manifest root must contain a 'layers' array");
            }

            var records = new List<LayerRecord>(layers.Count);
            for (var ordinal = 0; ordinal < layers.Count; ordinal++) {
                if (layers[ordinal] is not JsonObject layer) {
                    throw new InvalidDataException($"layer entry {ordinal} is not an object");
                }

                records.Add(ParseRecord(layer, stack.WorkDirectory, ordinal));
            }

            ValidateRecords(records);
            lock (stack._gate) {
                stack._records.AddRange(records);
                stack._nextIndex = ReadNextIndex(root, records, stack.WorkDirectory);
                stack._baseReady = root["baseReady"]?.GetValue<bool>() ?? false;
                stack._consolidationPending = root["consolidationPending"]?.GetValue<bool>() ?? false;
                stack._sourceFingerprint = root["sourceFingerprint"]?.GetValue<string>();
                stack._sourceIndex = root["sourceIndex"]?.GetValue<int>();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException
                                       or FormatException
                                       or InvalidDataException or OverflowException) {
            // A manifest we cannot read is not resumable: fail with the recovery hint
            // instead of a raw parser stack trace.
            throw new IOException(
                $"layer manifest '{stack.ManifestPath}' is corrupt ({ex.Message}); delete the workspace and rebuild.",
                ex);
        }

        return stack;
    }

    /// <summary>
    ///     Persists the manifest atomically (staging file + move): a crash mid-write
    ///     must not corrupt the resume state.
    /// </summary>
    public void Save() {
        lock (_gate) {
            Directory.CreateDirectory(WorkDirectory);
            var layers = new JsonArray();
            foreach (var record in _records) {
                layers.Add((JsonNode)record.ToJson());
            }

            var root = new JsonObject {
                ["layers"] = layers,
                ["nextIndex"] = _nextIndex,
                ["baseReady"] = _baseReady,
                ["consolidationPending"] = _consolidationPending,
                ["sourceFingerprint"] = _sourceFingerprint,
                ["sourceIndex"] = _sourceIndex
            };
            var staging = ManifestPath + ".tmp";
            File.WriteAllText(staging, root.ToPrettyString());
            File.Move(staging, ManifestPath, true);
        }
    }

    /// <summary>Creates the base layer VHDX; no-op when one already exists (resumable).</summary>
    public async Task EnsureBaseAsync(long maximumMb, string volumeLabel, CancellationToken ct) {
        bool baseReady;
        lock (_gate) {
            baseReady = _baseReady;
        }

        if (!baseReady) {
            // A base file without the completion marker may be the result of a crashed or
            // failed Apply-Image. Every diff depends on that base, so the whole incomplete
            // chain must be discarded before creating a new one.
            var stalePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_gate) {
                foreach (var record in _records.Where(r => r is { Index: > 0, VhdxPath: not null })) {
                    stalePaths.Add(record.VhdxPath!);
                }
            }

            if (Directory.Exists(WorkDirectory)) {
                foreach (var path in Directory.EnumerateFiles(WorkDirectory, "L*.vhdx")) {
                    stalePaths.Add(path);
                }
            }

            if (File.Exists(BaseVhdxPath)) {
                stalePaths.Add(BaseVhdxPath);
            }

            foreach (var path in stalePaths) {
                try {
                    await backend.DetachAsync(path, CancellationToken.None);
                }
                catch (Exception ex) {
                    log.Warn($"could not detach incomplete layer '{Path.GetFileName(path)}': {ex.Message}");
                }

                if (!TryDelete(path)) {
                    throw new IOException(
                        $"incomplete layer '{path}' could not be removed; delete the workspace and rebuild.");
                }
            }

            lock (_gate) {
                _records.Clear();
                _nextIndex = 1;
                _baseReady = false;
            }

            Save();
        }

        if (!File.Exists(BaseVhdxPath)) {
            bool hasDependentLayers;
            lock (_gate) {
                hasDependentLayers = _records.Any(r => r.Index > 0);
                if (!hasDependentLayers) {
                    _records.RemoveAll(r => r.Index == 0);
                    _baseReady = false;
                }
            }

            if (hasDependentLayers) {
                throw new IOException(
                    $"base layer '{BaseVhdxPath}' is missing while dependent layers remain; delete the workspace and rebuild.");
            }

            Save();
        }

        if (File.Exists(BaseVhdxPath)) {
            bool hasRecord;
            lock (_gate) {
                hasRecord = _records.Any(r => r is { Index: 0, Status: LayerStatus.Committed });
                _records.RemoveAll(r => r.Index == 0 && r.Status != LayerStatus.Committed);
                if (!hasRecord) {
                    _records.Insert(0, new() {
                        Index = 0,
                        Title = "base image (applied from source index)",
                        VhdxFileName = BaseFileName,
                        Status = LayerStatus.Committed,
                        VhdxPath = BaseVhdxPath
                    });
                }
            }

            if (!hasRecord) {
                Save();
            }

            log.Info($"reusing existing base layer {BaseFileName}");
            return;
        }

        Directory.CreateDirectory(WorkDirectory);
        try {
            await backend.CreateBaseAsync(BaseVhdxPath, maximumMb, volumeLabel, ct);
        }
        catch {
            try {
                await backend.DetachAsync(BaseVhdxPath, CancellationToken.None);
            }
            catch {
                /* best effort */
            }

            TryDelete(BaseVhdxPath);
            throw;
        }

        lock (_gate) {
            _records.Add(new() {
                Index = 0,
                StepId = null,
                Title = "base image (applied from source index)",
                VhdxFileName = BaseFileName,
                Status = LayerStatus.Committed,
                VhdxPath = BaseVhdxPath
            });
            _baseReady = false;
        }

        Save();
    }

    public async Task ApplyImageToBaseAsync(Func<string, CancellationToken, Task> applyAsync, CancellationToken ct) {
        var letter = await backend.AttachAsync(BaseVhdxPath, ct);
        try {
            await applyAsync($"{letter}:\\", ct);
        }
        catch {
            // A failed apply leaves the base unusable anyway; a detach error here (often
            // "not attached" after disk-full) must not mask the original apply failure.
            try {
                await backend.DetachAsync(BaseVhdxPath, CancellationToken.None);
            }
            catch (Exception ex) {
                log.Warn($"detach after a failed base apply also failed: {ex.Message}");
            }

            lock (_gate) {
                _baseReady = false;
            }

            Save();

            throw;
        }

        await backend.DetachAsync(BaseVhdxPath, CancellationToken.None);
        lock (_gate) {
            _baseReady = true;
        }

        Save();
    }

    /// <summary>
    ///     Removes the current base state while preserving source provenance. This is used by
    ///     layerless resume, where the completed prefix is replayed from a clean source image.
    /// </summary>
    public async Task ResetBaseAsync(CancellationToken ct) {
        lock (_gate) {
            if (_records.Any(record => record.Index > 0)) {
                throw new InvalidOperationException(
                    "cannot reset the base while differencing layers are present.");
            }
        }

        ct.ThrowIfCancellationRequested();
        try {
            await backend.DetachAsync(BaseVhdxPath, CancellationToken.None);
        }
        catch {
            // The base is normally detached already; deletion below is the authoritative check.
        }

        if (!TryDelete(BaseVhdxPath)) {
            throw new IOException($"could not reset base layer '{BaseVhdxPath}'.");
        }

        lock (_gate) {
            _records.Clear();
            _nextIndex = 1;
            _baseReady = false;
            _consolidationPending = false;
        }

        Save();
    }

    /// <summary>Sets or validates the source identity bound to this workspace.</summary>
    public void InitializeOrValidateSource(string sourceFingerprint, int sourceIndex) {
        if (string.IsNullOrWhiteSpace(sourceFingerprint)) {
            throw new ArgumentException("source fingerprint cannot be empty", nameof(sourceFingerprint));
        }

        lock (_gate) {
            if (_sourceFingerprint is null && _sourceIndex is null) {
                if (_records.Count > 0) {
                    throw new IOException(
                        $"layer workspace '{WorkDirectory}' has no source provenance; it cannot be resumed safely.");
                }

                _sourceFingerprint = sourceFingerprint;
                _sourceIndex = sourceIndex;
            }
            else if (!string.Equals(_sourceFingerprint, sourceFingerprint, StringComparison.Ordinal)
                     || _sourceIndex != sourceIndex) {
                throw new IOException(
                    $"layer workspace '{WorkDirectory}' belongs to a different source image or index; " +
                    "start a clean build instead of resuming it.");
            }
        }

        Save();
    }

    /// <summary>Creates + attaches a fresh differencing layer for one plan step.</summary>
    public async Task<LayerSession> BeginLayerAsync(string stepId, string title, CancellationToken ct,
        string? stepFingerprint = null) {
        var index = NextLayerIndex();
        var fileName = $"L{index:D3}.vhdx";
        var vhdxPath = Path.Combine(WorkDirectory, fileName);
        var parent = LeafVhdxPath;
        log.Info($"creating layer {index:000} '{title}' on {Path.GetFileName(parent)}", layerIndex: index);
        var record = new LayerRecord {
            Index = index,
            StepId = stepId,
            Title = title,
            VhdxFileName = fileName,
            Status = LayerStatus.Pending,
            VhdxPath = vhdxPath,
            StepFingerprint = stepFingerprint
        };
        lock (_gate) {
            _records.Add(record);
        }

        Save();

        char letter;
        try {
            await backend.CreateDiffAsync(vhdxPath, parent, ct);
            letter = await backend.AttachAsync(vhdxPath, ct);
        }
        catch (Exception ex) {
            try {
                await backend.DetachAsync(vhdxPath, CancellationToken.None);
            }
            catch {
                /* best effort */
            }

            var cleanupFailed = !TryDelete(vhdxPath);
            MarkFailedRecord(record.Index, ex.Message, !cleanupFailed);
            if (cleanupFailed) {
                log.Warn($"could not clean up failed layer {record.Index:000}; workspace retained for recovery",
                    layerIndex: record.Index);
            }

            throw;
        }

        return new() { Record = record, VhdxPath = vhdxPath, MountPath = $"{letter}:\\" };
    }

    /// <summary>Detaches and keeps the layer (plan succeeded).</summary>
    public async Task CommitLayerAsync(LayerSession session, JsonObject? operationResult, CancellationToken ct) {
        await backend.DetachAsync(session.VhdxPath, CancellationToken.None);
        lock (_gate) {
            UpdateRecord(session.Record.Index, record => record with {
                Status = LayerStatus.Committed,
                EndedUtc = DateTimeOffset.UtcNow,
                OperationResult = operationResult ?? record.OperationResult
            });
        }

        Save();
        log.Info(
            $"layer {session.Record.Index:000} committed ({new FileInfo(session.VhdxPath).Length / 1024.0 / 1024:F1} MB)",
            layerIndex: session.Record.Index);
        // Merging is export-time work; mid-build it is only a safety valve for backends
        // whose deep-chain handling is unreliable (see ILayerBackend.MaxSafeChainDepth).
        if (ChainDepth > backend.MaxSafeChainDepth) {
            await ConsolidateAsync(ct);
        }
    }

    /// <summary>Detaches and deletes the layer — the image state equals pre-step (atomic rollback).</summary>
    public async Task DiscardLayerAsync(LayerSession session, string? error) {
        Exception? detachError = null;
        try {
            await backend.DetachAsync(session.VhdxPath, CancellationToken.None);
        }
        catch (Exception ex) {
            detachError = ex;
            log.Warn($"detach failed while discarding layer {session.Record.Index:000}: {ex.Message}");
        }

        var deleteFailed = !TryDelete(session.VhdxPath);
        if (deleteFailed) {
            var cleanupMessage =
                $"layer cleanup failed (detach: {detachError?.Message ?? "ok"}); original error: {error}";
            MarkFailedRecord(session.Record.Index, cleanupMessage, false);
            throw new IOException(
                $"could not discard layer {session.Record.Index:000}; the workspace was retained for recovery.",
                detachError);
        }

        lock (_gate) {
            UpdateRecord(session.Record.Index, record => record with {
                Status = LayerStatus.Discarded,
                EndedUtc = DateTimeOffset.UtcNow,
                Error = error
            });
        }

        Save();
        log.Warn($"layer {session.Record.Index:000} discarded; image state unchanged",
            layerIndex: session.Record.Index);
    }

    /// <summary>
    ///     Resume support: deletes every diff layer above <paramref name="keepIndex" /> (files + records) so the
    ///     chain ends at a known checkpoint. The common prefix stays byte-identical; new layers branch on top.
    /// </summary>
    public async Task TruncateToAsync(int keepIndex, CancellationToken ct) {
        if (keepIndex < 0) {
            throw new ArgumentOutOfRangeException(nameof(keepIndex), keepIndex, "keep index cannot be negative");
        }

        List<LayerRecord> drop;
        lock (_gate) {
            drop = [.. _records.Where(r => r.Index > keepIndex)];
        }

        if (drop.Any(r => r.Status == LayerStatus.Merged)) {
            throw new InvalidOperationException(
                "cannot truncate a workspace after layers were merged into the base; start a clean build.");
        }

        var removed = new HashSet<int>();
        foreach (var record in drop.Where(r => r.VhdxFileName != BaseFileName).OrderByDescending(r => r.Index)) {
            ct.ThrowIfCancellationRequested();
            if (record.VhdxPath is { } path) {
                try {
                    await backend.DetachAsync(path, CancellationToken.None);
                }
                catch {
                    /* best effort */
                }

                var deleteFailed = !TryDelete(path);
                if (deleteFailed) {
                    MarkFailedRecord(record.Index, "truncated; layer file could not be deleted", false);
                }
                else {
                    removed.Add(record.Index);
                }
            }
        }

        lock (_gate) {
            _records.RemoveAll(r => removed.Contains(r.Index));
        }

        Save();
        if (drop.Count > 0) {
            log.Info($"resume: truncated {drop.Count} diverged layer(s) above index {keepIndex:000}");
        }
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
        if (diffs.Any(diff => diff.VhdxPath is null || !File.Exists(diff.VhdxPath))) {
            throw new IOException("cannot consolidate the layer chain because a committed VHDX is missing");
        }

        lock (_gate) {
            _consolidationPending = true;
        }

        Save();

        try {
            var leaf = diffs[^1];
            await backend.MergeAsync(leaf.VhdxPath!, depth, ct);
            foreach (var diff in diffs) {
                var deleteFailed = !TryDelete(diff.VhdxPath!);
                lock (_gate) {
                    UpdateRecord(diff.Index, record => record with {
                        Status = LayerStatus.Merged,
                        EndedUtc = DateTimeOffset.UtcNow,
                        Error = deleteFailed ? "merged; VHDX cleanup is still pending" : record.Error
                    });
                }
            }

            lock (_gate) {
                _consolidationPending = false;
            }

            Save();
        }
        catch (Exception ex) {
            log.Error($"layer consolidation did not complete; workspace requires a clean rebuild: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    ///     Consolidates the chain and copies the merged base to <paramref name="targetPath" />
    ///     (a boot-testable VHDX artifact).
    /// </summary>
    public async Task ExportMergedVhdxAsync(string targetPath, CancellationToken ct) {
        await ConsolidateAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(BaseVhdxPath, targetPath, true);
    }

    /// <summary>Replaces one record in place (caller must hold <see cref="_gate" />).</summary>
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

            var record = _records.LastOrDefault(r => r.Index == index
                                                     && r.Status is LayerStatus.Committed or LayerStatus.Merged)
                         ?? throw new ArgumentException($"layer {index:000} is not committed");
            return record is { Status: LayerStatus.Committed, VhdxPath: not null } && File.Exists(record.VhdxPath)
                ? record.VhdxPath
                : BaseVhdxPath; // merged-away layer: its content is in the base
        }
    }

    /// <summary>
    ///     Deletes a layer file. A locked file is only disk-space debt — layer indexes are
    ///     monotonic, so the name is never reused — and must not mask the caller's real error.
    /// </summary>
    private bool TryDelete(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) {
            log.Warn($"could not delete layer file '{path}' ({ex.Message}); leaving it behind");
            return false;
        }
    }

    private int NextLayerIndex() {
        lock (_gate) {
            return checked(_nextIndex++);
        }
    }

    private static int ReadNextIndex(JsonObject root, IReadOnlyList<LayerRecord> records, string workDirectory) {
        var next = records.Count == 0 ? 1 : checked(records.Max(r => r.Index) + 1);
        if (root["nextIndex"] is { } nextNode) {
            try {
                var persisted = nextNode.GetValue<int>();
                if (persisted < 1) {
                    throw new InvalidDataException("layer manifest 'nextIndex' must be positive");
                }

                next = Math.Max(next, persisted);
            }
            catch (Exception ex) {
                throw new InvalidDataException("layer manifest has invalid 'nextIndex'", ex);
            }
        }

        if (!Directory.Exists(workDirectory)) {
            return next;
        }

        foreach (var path in Directory.EnumerateFiles(workDirectory, "L*.vhdx")) {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > 1
                && int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)) {
                next = Math.Max(next, checked(index + 1));
            }
        }

        return next;
    }

    private void MarkFailedRecord(int index, string? error, bool discarded) {
        lock (_gate) {
            UpdateRecord(index, record => record with {
                Status = discarded ? LayerStatus.Discarded : LayerStatus.Pending,
                EndedUtc = discarded ? DateTimeOffset.UtcNow : null,
                Error = error
            });
        }

        Save();
    }

    private static LayerRecord ParseRecord(JsonObject node, string workDirectory, int ordinal) {
        var index = RequiredInt(node, "index", ordinal);
        if (index < 0) {
            throw new InvalidDataException($"layer entry {ordinal} has a negative index");
        }

        var fileName = RequiredString(node, "vhdx", ordinal);
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || Path.IsPathFullyQualified(fileName)
            || (!string.Equals(fileName, BaseFileName, StringComparison.OrdinalIgnoreCase)
                && !fileName.StartsWith("L", StringComparison.OrdinalIgnoreCase))
            || !fileName.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
            || (index > 0 && !fileName.Equals($"L{index:D3}.vhdx", StringComparison.OrdinalIgnoreCase))) {
            throw new InvalidDataException($"layer entry {ordinal} has an invalid VHDX file name");
        }

        if (!Enum.TryParse<LayerStatus>(RequiredString(node, "status", ordinal), true, out var status)
            || !Enum.IsDefined(status)) {
            throw new InvalidDataException($"layer entry {ordinal} has an invalid status");
        }

        var started = ParseTimestamp(node, "startedUtc", ordinal);
        DateTimeOffset? ended = node["endedUtc"] is null ? null : ParseTimestamp(node, "endedUtc", ordinal);
        return new() {
            Index = index,
            StepId = OptionalString(node, "stepId", ordinal),
            Title = OptionalString(node, "title", ordinal),
            VhdxFileName = fileName,
            Status = status,
            StartedUtc = started,
            EndedUtc = ended,
            OperationResult = node["operationResult"] is null
                ? null
                : node["operationResult"] as JsonObject
                  ?? throw new InvalidDataException($"layer entry {ordinal} has invalid operationResult"),
            Error = OptionalString(node, "error", ordinal),
            StepFingerprint = OptionalString(node, "stepFingerprint", ordinal),
            VhdxPath = Path.Combine(workDirectory, fileName)
        };
    }

    private static void ValidateRecords(IReadOnlyList<LayerRecord> records) {
        if (records.Count == 0) {
            return;
        }

        if (records[0].Index != 0
            || !records[0].VhdxFileName.Equals(BaseFileName, StringComparison.OrdinalIgnoreCase)
            || records[0].Status != LayerStatus.Committed) {
            throw new InvalidDataException("layer manifest must start with a committed base.vhdx layer");
        }

        var previous = -1;
        var indexes = new HashSet<int>();
        foreach (var record in records) {
            if (!indexes.Add(record.Index) || record.Index <= previous) {
                throw new InvalidDataException("layer indexes must be unique and strictly increasing");
            }

            previous = record.Index switch {
                0 when !record.VhdxFileName.Equals(BaseFileName, StringComparison.OrdinalIgnoreCase) =>
                    throw new InvalidDataException("layer zero must use base.vhdx"),
                > 0 when record.VhdxFileName.Equals(BaseFileName, StringComparison.OrdinalIgnoreCase) =>
                    throw new InvalidDataException("only layer zero may use base.vhdx"),
                _ => record.Index
            };
        }
    }

    private static int RequiredInt(JsonObject node, string name, int ordinal) {
        try {
            return node[name]?.GetValue<int>() ??
                   throw new InvalidDataException($"layer entry {ordinal} is missing '{name}'");
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception ex) {
            throw new InvalidDataException($"layer entry {ordinal} has invalid '{name}'", ex);
        }
    }

    private static string RequiredString(JsonObject node, string name, int ordinal) {
        try {
            return node[name]?.GetValue<string>() ??
                   throw new InvalidDataException($"layer entry {ordinal} is missing '{name}'");
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception ex) {
            throw new InvalidDataException($"layer entry {ordinal} has invalid '{name}'", ex);
        }
    }

    private static string? OptionalString(JsonObject node, string name, int ordinal) =>
        node[name] is null ? null : RequiredString(node, name, ordinal);

    private static DateTimeOffset ParseTimestamp(JsonObject node, string name, int ordinal) {
        var value = RequiredString(node, name, ordinal);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out var result)
            ? result
            : throw new InvalidDataException($"layer entry {ordinal} has invalid '{name}'");
    }
}
