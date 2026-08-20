using TinyWin2.Core.Env;

namespace TinyWin2.Core.Tests;

/// <summary>
/// Skips integration tests unless TINYWIN2_IT=1 on Windows with an admin shell; set
/// <see cref="RequiresTestSource"/> to also demand TINYWIN2_TEST_ISO (ISO file or media
/// folder) and enough free space on the test root drive (TINYWIN2_TEST_ROOT or %TEMP%).
/// </summary>
public sealed class ItGateAttribute : SkipAttribute {
    private const long RequiredFreeBytes = 55L * 1024 * 1024 * 1024;

    public bool RequiresTestSource { get; init; }

    public ItGateAttribute() : base("integration tests are gated behind TINYWIN2_IT=1") {
    }

    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(GateIsClosed(RequiresTestSource));

    protected override string GetSkipReason(TestRegisteredContext context) =>
        Environment.GetEnvironmentVariable("TINYWIN2_IT") != "1"
            ? "integration tests are gated behind TINYWIN2_IT=1"
            : !OperatingSystem.IsWindows() || !EnvironmentDoctor.IsAdministrator()
                ? "integration tests need Windows and an administrator shell"
                : SourceMissing()
                    ? "needs TINYWIN2_TEST_ISO pointing at an ISO file or unpacked media folder"
                    : "not enough free space for an image build (set TINYWIN2_TEST_ROOT to a drive with 55+ GB free)";

    internal static bool GateIsClosed(bool requiresTestSource) =>
        Environment.GetEnvironmentVariable("TINYWIN2_IT") != "1"
        || !OperatingSystem.IsWindows()
        || !EnvironmentDoctor.IsAdministrator()
        || (requiresTestSource && (SourceMissing() || SpaceMissing()));

    private static bool SourceMissing() {
        var source = Environment.GetEnvironmentVariable("TINYWIN2_TEST_ISO") ?? "";
        return !File.Exists(source) && !Directory.Exists(source);
    }

    private static bool SpaceMissing() =>
        OperatingSystem.IsWindows()
        && !EnvironmentDoctor.CheckFreeSpace(TestRoot(), RequiredFreeBytes).Ok;

    /// <summary>Big-disk support: the ISO builds need far more room than %TEMP% usually has.</summary>
    internal static string TestRoot() {
        var configured = Environment.GetEnvironmentVariable("TINYWIN2_TEST_ROOT");
        return string.IsNullOrWhiteSpace(configured) ? Path.GetTempPath() : Path.GetFullPath(configured);
    }

    /// <summary>Creates a fresh workspace under the configured test root.</summary>
    internal static string CreateTestRoot() {
        var path = Path.Combine(TestRoot(), "tinywin2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string TestSource() => Environment.GetEnvironmentVariable("TINYWIN2_TEST_ISO") ?? "";
}
