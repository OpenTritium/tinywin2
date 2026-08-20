using System.Text.Json.Nodes;
using TinyWin2.Core.Executers;
using TinyWin2.Core.Executers.Dism;
using TinyWin2.Core.Executers.Driver;
using TinyWin2.Core.Executers.Fs;
using TinyWin2.Core.Executers.Registry;

namespace TinyWin2.Core.Tests;

/// <summary>
/// Boundary validation: every malformed `with` payload is rejected at the single
/// JsonNode→record edge, before any executer logic runs.
/// </summary>
public sealed class OptionsValidationTests {
    [Test]
    public async Task RegistryServiceRejectsUnknownStartMode() {
        var ex = Assert.Throws<ExecException>(() =>
            RegistryServiceOptions.FromDesired(new JsonObject {
                ["services"] = new JsonArray("Svc"),
                ["start"] = "sometimes",
            }))!;

        await Assert.That(ex.Message).Contains("auto|delayedAuto|manual|disabled");
    }

    [Test]
    public async Task RegistryServiceRequiresTargetList() {
        var ex = Assert.Throws<ExecException>(() =>
            RegistryServiceOptions.FromDesired(new JsonObject { ["start"] = "auto" }))!;

        await Assert.That(ex.Message).Contains("'services' or 'servicePatterns'");
    }

    [Test]
    public async Task RegistryServiceRejectsInvalidCharacters() {
        var ex = Assert.Throws<ExecException>(() =>
            RegistryServiceOptions.FromDesired(new JsonObject {
                ["services"] = new JsonArray("Bad Service!"),
                ["start"] = "auto",
            }))!;

        await Assert.That(ex.Message).Contains("invalid service name or pattern");
    }

    [Test]
    public async Task RegistryServiceExposesStartDwordAndDelayedFlag() {
        var options = RegistryServiceOptions.FromDesired(new JsonObject {
            ["services"] = new JsonArray("Svc"),
            ["start"] = "delayedAuto",
        });

        await Assert.That(options.StartDword).IsEqualTo(2);
        await Assert.That(options.IsDelayed).IsTrue();
    }

    [Test]
    public async Task RegistryValueNormalizesSingleValueShorthand() {
        var options = RegistryValueOptions.FromDesired(new JsonObject {
            ["hive"] = "software",
            ["key"] = "K",
            ["name"] = "V",
            ["type"] = "dword",
            ["data"] = 1,
        }, Ensure.Present);

        await Assert.That(options.Values.Count).IsEqualTo(1);
        await Assert.That(options.Values[0].RegType).IsEqualTo("REG_DWORD");
    }

    [Test]
    public async Task RegistryValueRejectsDeleteKeysInPresentMode() {
        var ex = Assert.Throws<ExecException>(() =>
            RegistryValueOptions.FromDesired(new JsonObject {
                ["hive"] = "software",
                ["deleteKeys"] = new JsonArray("K"),
            }, Ensure.Present))!;

        await Assert.That(ex.Message).Contains("only valid with ensure: absent");
    }

    [Test]
    public async Task RegistryValueRejectsUnknownHive() {
        var ex = Assert.Throws<ExecException>(() =>
            RegistryValueOptions.FromDesired(new JsonObject {
                ["hive"] = "hive_of_hades",
                ["values"] = new JsonArray(),
            }, Ensure.Present))!;

        await Assert.That(ex.Message).Contains("unsupported registry hive");
    }

    [Test]
    public async Task FeatureDefaultsRemovePayloadAndValidatesList() {
        var options = FeatureOptions.FromDesired(new JsonObject {
            ["features"] = new JsonArray("Microsoft-Hyper-V"),
        });
        await Assert.That(options.RemovePayload).IsTrue();

        var ex = Assert.Throws<ExecException>(() =>
            FeatureOptions.FromDesired(new JsonObject()))!;
        await Assert.That(ex.Message).Contains("'features'");
    }

    [Test]
    public async Task PackagePrecompilesAndRejectsBadRegex() {
        var options = PackageOptions.FromDesired(new JsonObject {
            ["patterns"] = new JsonArray("^Foo~"),
        });
        await Assert.That(options.Patterns[0].IsMatch("Foo~123")).IsTrue();

        var ex = Assert.Throws<ExecException>(() =>
            PackageOptions.FromDesired(new JsonObject { ["patterns"] = new JsonArray("(unclosed") }))!;
        await Assert.That(ex.Message).Contains("invalid package pattern");
    }

    [Test]
    public async Task DriverStoreRejectsPathedInfNames() {
        var ex = Assert.Throws<ExecException>(() =>
            DriverStoreOptions.FromDesired(new JsonObject {
                ["infNames"] = new JsonArray("C:\\evil\\path.inf"),
            }))!;

        await Assert.That(ex.Message).Contains("invalid driver INF name");
    }

    [Test]
    public async Task FsPathValidatesPerEnsureShape() {
        var absent = FsPathOptions.FromDesired(new JsonObject {
            ["paths"] = new JsonArray("inetpub"),
        }, Ensure.Absent);
        await Assert.That(absent.Paths.Count).IsEqualTo(1);

        var ex = Assert.Throws<ExecException>(() =>
            FsPathOptions.FromDesired(new JsonObject { ["paths"] = new JsonArray("x") }, Ensure.Present))!;
        await Assert.That(ex.Message).Contains("'path'");
    }
}
