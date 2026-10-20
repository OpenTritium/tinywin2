namespace TinyWin2.Core.Tests;

/// <summary>
///     cmd.exe parses .cmd/.bat files strictly by CRLF; an LF-only asset silently turns every line into a
///     fragment and the hook exits without applying anything (observed with kiosk-firstlogon.cmd).
/// </summary>
public sealed class PlanAssetLineEndingTests {
    [Test]
    public async Task BatchAssetsUseCrLf() {
        var assets = LocateAssetsDirectory();
        var batchFiles = Directory.EnumerateFiles(assets, "*.cmd", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(assets, "*.bat", SearchOption.AllDirectories))
            .ToList();
        await Assert.That(batchFiles.Count).IsGreaterThan(0);

        foreach (var file in batchFiles) {
            var bytes = File.ReadAllBytes(file);
            for (var index = 0; index < bytes.Length; index++) {
                if (bytes[index] == (byte)'\n' && (index == 0 || bytes[index - 1] != (byte)'\r')) {
                    throw new InvalidOperationException(
                        $"{file} contains a bare LF at offset {index}; cmd.exe requires CRLF.");
                }
            }
        }
    }

    private static string LocateAssetsDirectory() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            var candidate = Path.Combine(directory.FullName, "plans", "assets");
            if (Directory.Exists(candidate)) {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"could not locate plans/assets above '{AppContext.BaseDirectory}'.");
    }
}
