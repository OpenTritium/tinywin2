using System.Text.Json.Nodes;

namespace TinyWin2.Core.Executers.Fs;

/// <summary>
///     The fs.path asset knowledge that step fingerprinting needs: which operations
///     reference a bundled asset and where that asset resolves under the plan's assets
///     root. Keeps the build engine from reading fs.path spec internals.
/// </summary>
internal static class FsPathAssets {
    /// <summary>The copy source of an fs.path copy operation; null for anything else.</summary>
    internal static string? GetCopyAssetSource(OperationSpec operation) {
        if (operation is not { Resource: "fs.path", Action: OperationAction.Copy }) {
            return null;
        }

        return operation.Spec.TryGetPropertyValue("source", out var node)
               && node is JsonValue value
               && value.TryGetValue<string>(out var source)
            ? source
            : null;
    }

    /// <summary>
    ///     Resolves an asset source under the plan's assets root with the same containment
    ///     rules the executer enforces; null when it escapes or no assets root is available.
    /// </summary>
    internal static string? ResolveAssetPath(string? assetsRoot, string? source) {
        if (assetsRoot is null || source is null) {
            return null;
        }

        return SafePath.TryResolveInside(assetsRoot, source);
    }
}
