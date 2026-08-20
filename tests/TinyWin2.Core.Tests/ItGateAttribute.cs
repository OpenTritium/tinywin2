using TinyWin2.Core.Env;

namespace TinyWin2.Core.Tests;

/// <summary>
/// Skips integration tests unless TINYWIN2_IT=1 on Windows with an admin shell; set
/// <see cref="RequiresTestSource"/> to also demand TINYWIN2_TEST_ISO (ISO file or media folder).
/// </summary>
public sealed class ItGateAttribute : SkipAttribute {
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
                : "needs TINYWIN2_TEST_ISO pointing at an ISO file or unpacked media folder";

    private static bool GateIsClosed(bool requiresTestSource) =>
        Environment.GetEnvironmentVariable("TINYWIN2_IT") != "1"
        || !OperatingSystem.IsWindows()
        || !EnvironmentDoctor.IsAdministrator()
        || (requiresTestSource && SourceMissing());

    private static bool SourceMissing() {
        var source = Environment.GetEnvironmentVariable("TINYWIN2_TEST_ISO") ?? "";
        return !File.Exists(source) && !Directory.Exists(source);
    }

    internal static string TestSource() => Environment.GetEnvironmentVariable("TINYWIN2_TEST_ISO") ?? "";
}
