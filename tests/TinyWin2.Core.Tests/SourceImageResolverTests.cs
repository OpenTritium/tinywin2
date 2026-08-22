using System.Text.Json.Nodes;
using TinyWin2.Core.Logging;
using TinyWin2.Core.Pipeline;

namespace TinyWin2.Core.Tests;

public sealed class SourceImageResolverTests : IDisposable {
    private readonly string _root = TestPlans.CreateTempDirectory();
    private readonly FakeProcessRunner _runner = new();
    private readonly SourceImageResolver _resolver;

    public SourceImageResolverTests() {
        _resolver = new SourceImageResolver(_runner, new BuildLog());
    }

    // ---- pure parsers ------------------------------------------------------

    [Test]
    public async Task ParseKeyValueLinesSkipsNoiseAndTrims() {
        var output = """

            Index : 1
            Name : ServerStandard

            not a colon line
            Version: 10.0.26100.1

            """;
        var fields = SourceImageResolver.ParseKeyValueLines(output);
        await Assert.That(fields["Index"]).IsEqualTo("1");
        await Assert.That(fields["Name"]).IsEqualTo("ServerStandard");
        await Assert.That(fields["Version"]).IsEqualTo("10.0.26100.1");
        await Assert.That(fields.ContainsKey("not a colon line")).IsFalse();
    }

    [Test]
    public async Task ParseByteSizeHandlesThousandsSeparatorsAndFallbacks() {
        await Assert.That(SourceImageResolver.ParseByteSize("11,831,247,965 bytes", 0)).IsEqualTo(11_831_247_965);
        await Assert.That(SourceImageResolver.ParseByteSize("42 bytes", 7)).IsEqualTo(42);
        await Assert.That(SourceImageResolver.ParseByteSize(null, 7)).IsEqualTo(7);
        await Assert.That(SourceImageResolver.ParseByteSize("no digits", 9)).IsEqualTo(9);
    }

    // ---- folder sources ----------------------------------------------------

    [Test]
    public async Task FolderSourcePrefersInstallWimOverEsd() {
        var media = CreateMediaFolder(installWim: true, installEsd: true);
        var source = await _resolver.ResolveAsync(media, CancellationToken.None);
        await Assert.That(source.IsMountedIso).IsFalse();
        await Assert.That(source.InstallImagePath).IsEqualTo(Path.Combine(media, "sources", "install.wim"));
        await Assert.That(_runner.Calls).IsEmpty();
    }

    [Test]
    public async Task FolderSourceFallsBackToEsd() {
        var media = CreateMediaFolder(installWim: false, installEsd: true);
        var source = await _resolver.ResolveAsync(media, CancellationToken.None);
        await Assert.That(source.InstallImagePath).IsEqualTo(Path.Combine(media, "sources", "install.esd"));
        await Assert.That(source.IsEsd).IsTrue();
    }

    [Test]
    public async Task FolderSourceWithoutInstallImageThrows() {
        var media = CreateMediaFolder(installWim: false, installEsd: false);
        var ex = Assert.Throws<FileNotFoundException>(() => _resolver.ResolveAsync(media, CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("install.wim");
    }

    [Test]
    public async Task FolderSourceWithoutBootWimIsRejected() {
        var media = CreateMediaFolder(installWim: true, installEsd: false, bootWim: false);
        var ex = Assert.Throws<FileNotFoundException>(() => _resolver.ResolveAsync(media, CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("boot.wim");
    }

    [Test]
    public async Task SourceThatIsNeitherFolderNorIsoThrows() {
        var ex = Assert.Throws<FileNotFoundException>(() =>
            _resolver.ResolveAsync(Path.Combine(_root, "ghost.iso"), CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("neither a folder nor an .iso");
    }

    // ---- index listing -----------------------------------------------------

    [Test]
    public async Task GetIndexesMergesSummaryWithPerIndexDetails() {
        _runner.Handler = (_, args) => args.Contains("/Index:1")
            ? FakeProcessRunner.Ok("""
                Name : ServerStandard Eval
                Description : Server Standard Evaluation
                Architecture : x64
                Version : 10.0.26100.1
                Edition ID : ServerStandardEval
                Size : 11,831,247,965 bytes

                """)
            : args.Contains("/Index:2")
                ? FakeProcessRunner.Ok("""
                    Name : ServerDatacenter Eval
                    Architecture : x64

                    """)
                : FakeProcessRunner.Ok("""
                    Index : 1
                    Name : stub one

                    Index : 2
                    Name : stub two

                    """);
        var indexes = await _resolver.GetIndexesAsync(Path.Combine(_root, "install.wim"), CancellationToken.None);
        await Assert.That(indexes).Count().IsEqualTo(2);
        await Assert.That(indexes[0].Index).IsEqualTo(1);
        await Assert.That(indexes[0].Name).IsEqualTo("ServerStandard Eval");
        await Assert.That(indexes[0].EditionId).IsEqualTo("ServerStandardEval");
        await Assert.That(indexes[0].SizeBytes).IsEqualTo(11_831_247_965);
        await Assert.That(indexes[1].Name).IsEqualTo("ServerDatacenter Eval");
        await Assert.That(indexes[1].SizeBytes).IsEqualTo(0);
    }

    [Test]
    public async Task GetIndexesFallsBackToSummaryWhenDetailQueryFails() {
        _runner.Handler = (_, args) => args.Contains("/Index:")
            ? FakeProcessRunner.Fail(1)
            : FakeProcessRunner.Ok("Index : 3\nName : summary name\n");
        var indexes = await _resolver.GetIndexesAsync(Path.Combine(_root, "install.wim"), CancellationToken.None);
        await Assert.That(indexes).Count().IsEqualTo(1);
        await Assert.That(indexes[0].Name).IsEqualTo("summary name");
    }

    [Test]
    public async Task GetIndexesThrowsWhenDismCannotReadImage() {
        _runner.Handler = (_, _) => FakeProcessRunner.Fail(2);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _resolver.GetIndexesAsync(Path.Combine(_root, "install.wim"), CancellationToken.None).GetAwaiter().GetResult());
        await Assert.That(ex.Message).Contains("could not read image info");
    }

    [Test]
    public async Task GetIndexQueriesOnlyTheSelectedIndex() {
        _runner.Handler = (_, args) => FakeProcessRunner.Ok("""
            Index : 3
            Name : ServerDatacenter Eval
            Description : Datacenter image
            Architecture : x64
            Version : 10.0.26100.1
            Edition ID : ServerDatacenterEval
            Size : 42 bytes
            """);

        var index = await _resolver.GetIndexAsync(
            Path.Combine(_root, "install.wim"), 3, CancellationToken.None);

        await Assert.That(_runner.Calls).Count().IsEqualTo(1);
        await Assert.That(_runner.ArgsOf(0)).Contains("/Index:3");
        await Assert.That(index.Name).IsEqualTo("ServerDatacenter Eval");
        await Assert.That(index.EditionId).IsEqualTo("ServerDatacenterEval");
        await Assert.That(index.SizeBytes).IsEqualTo(42);
    }

    // ---- ESD export --------------------------------------------------------

    [Test]
    public async Task ExportIndexSkipsWhenTargetAlreadyExists() {
        var target = Path.Combine(_root, "staging.wim");
        await File.WriteAllTextAsync(target, "existing");
        var source = "src.esd";
        var sourceFullPath = Path.GetFullPath(source);
        await File.WriteAllTextAsync(target + ".tinywin2.json", new JsonObject {
            ["source"] = sourceFullPath,
            ["sourceStamp"] = $"missing:{sourceFullPath}",
            ["index"] = 3,
            ["compress"] = "fast",
        }.ToJsonString());
        var result = await _resolver.ExportIndexToWimAsync(source, 3, target, fast: true,
            CancellationToken.None);
        await Assert.That(result).IsEqualTo(target);
        await Assert.That(_runner.Called("dism.exe")).IsFalse();
    }

    [Test]
    public async Task ExportIndexBuildsDismArguments() {
        var target = Path.Combine(_root, "staging.wim");
        _runner.Handler = (_, args) => {
            const string prefix = "/DestinationImageFile:";
            var argument = args.First(a => a.StartsWith(prefix, StringComparison.Ordinal));
            var destination = argument[prefix.Length..];
            File.WriteAllText(destination, "exported");
            return FakeProcessRunner.Ok();
        };
        var result = await _resolver.ExportIndexToWimAsync("src.esd", 3, target, fast: false, CancellationToken.None);
        await Assert.That(result).IsEqualTo(target);
        await Assert.That(_runner.Calls).Count().IsEqualTo(1);
        await Assert.That(string.Join(' ', _runner.ArgsOf(0))).Contains("/Export-Image");
        await Assert.That(string.Join(' ', _runner.ArgsOf(0))).Contains("/SourceIndex:3");
        await Assert.That(string.Join(' ', _runner.ArgsOf(0))).Contains("/Compress:max");
    }

    [Test]
    public async Task DismountEscapesApostrophesInIsoPaths() {
        var media = new SourceMedia {
            RootPath = "X:\\",
            IsMountedIso = true,
            IsoPath = "C:\\source's.iso",
            InstallImagePath = "X:\\sources\\install.wim",
        };

        await _resolver.DismountIsoAsync(media, CancellationToken.None);

        var command = string.Join(' ', _runner.ArgsOf(0));
        await Assert.That(command).Contains("C:\\source''s.iso");
        await Assert.That(command).Contains("-ErrorAction Stop");
    }

    private string CreateMediaFolder(bool installWim, bool installEsd, bool bootWim = true) {
        var media = Path.Combine(_root, "media");
        Directory.CreateDirectory(Path.Combine(media, "sources"));
        if (installWim) {
            File.WriteAllText(Path.Combine(media, "sources", "install.wim"), "wim");
        }
        if (installEsd) {
            File.WriteAllText(Path.Combine(media, "sources", "install.esd"), "esd");
        }
        if (bootWim) {
            File.WriteAllText(Path.Combine(media, "sources", "boot.wim"), "boot");
        }
        return media;
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
