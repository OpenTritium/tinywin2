using TinyWin2.Core.Env;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Layers;
using TinyWin2.Core.Native;

namespace TinyWin2.Core.Tests;

/// <summary>
/// Integration tests gated behind TINYWIN2_IT=1 (+ admin). They create real VHDX layers,
/// so they only run on a Windows host with diskpart and enough temp space.
/// </summary>
public sealed class LayerBackendIntegrationTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("TINYWIN2_IT") == "1"
        && OperatingSystem.IsWindows()
        && EnvironmentDoctor.IsAdministrator();

    private static string TestIso =>
        Environment.GetEnvironmentVariable("TINYWIN2_TEST_ISO") ?? "";

    [Test]
    public async Task VhdCreateAttachWriteDetachRoundtrip()
    {
        if (!Enabled)
        {
            return; // integration tests gated behind TINYWIN2_IT=1
        }

        var runner = new ProcessRunner();
        var backend = new DiskPartVhdBackend(runner);
        var directory = TestPlans.CreateTempDirectory();
        try
        {
            var baseVhdx = Path.Combine(directory, "base.vhdx");
            await backend.CreateBaseAsync(baseVhdx, 512, "tinywin2-it", CancellationToken.None);

            await Assert.That(File.Exists(baseVhdx)).IsTrue();

            var letter = DiskPartVhdBackend.FreeDriveLetters().First();
            await backend.AttachAsync(baseVhdx, letter.ToString(), CancellationToken.None);
            try
            {
                var probe = $"{letter}:\\probe.txt";
                await File.WriteAllTextAsync(probe, "hello");
                await Assert.That(File.Exists(probe)).IsTrue();
            }
            finally
            {
                await backend.DetachAsync(baseVhdx, CancellationToken.None);
            }

            // Differencing layer sees the base content.
            var diff = Path.Combine(directory, "L001.vhdx");
            await backend.CreateDiffAsync(diff, baseVhdx, CancellationToken.None);
            await backend.AttachAsync(diff, letter.ToString(), CancellationToken.None);
            try
            {
                await Assert.That(File.Exists($"{letter}:\\probe.txt")).IsTrue();
                await File.WriteAllTextAsync($"{letter}:\\probe2.txt", "layer2");
            }
            finally
            {
                await backend.DetachAsync(diff, CancellationToken.None);
            }

            // Base must be untouched by the layer write.
            await backend.AttachAsync(baseVhdx, letter.ToString(), CancellationToken.None);
            try
            {
                await Assert.That(File.Exists($"{letter}:\\probe.txt")).IsTrue();
                await Assert.That(File.Exists($"{letter}:\\probe2.txt")).IsFalse();
            }
            finally
            {
                await backend.DetachAsync(baseVhdx, CancellationToken.None);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task MiniBuildAgainstBootWimIndex1()
    {
        if (!Enabled || !File.Exists(TestIso))
        {
            return; // needs TINYWIN2_IT=1 and TINYWIN2_TEST_ISO
        }

        var runner = new ProcessRunner();
        var executers = new ExecuterRegistry(runner);
        var backend = new DiskPartVhdBackend(runner);
        var outputRoot = Path.Combine(Path.GetTempPath(), "tinywin2-it-" + Guid.NewGuid().ToString("N"));

        var plansDir = TestPlans.CreateTempDirectory();
        TestPlans.WritePlan(plansDir, "it.registry-probe", o =>
        {
            o["execs"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["resource"] = "registry.value",
                ["ensure"] = "present",
                ["with"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["hive"] = "software",
                    ["key"] = "SOFTWARE\\TinyWin2IT",
                    ["name"] = "Probe",
                    ["type"] = "dword",
                    ["data"] = 42,
                },
            });
        });
        var catalog = Plans.PlanCatalog.LoadDirectory(plansDir);

        var engine = new Pipeline.BuildEngine(runner, executers, backend,
            new Logging.BuildLog { EchoConsole = true });
        var result = await engine.BuildAsync(new Pipeline.BuildOptions
        {
            SourcePath = TestIso,
            ImageIndex = 1, // boot.wim index 1 (WinPE) — light enough for CI-ish validation
            Selections = [new Plans.PlanSelection("it.registry-probe")],
            OutputRoot = outputRoot,
            Catalog = catalog,
            OutputMode = Pipeline.OutputMode.Wim,
            Fast = true,
            PlansDirectory = plansDir,
            BaseVhdxMaximumMb = 8_192,
        }, CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(File.Exists(result.InstallImagePath)).IsTrue();
    }
}
