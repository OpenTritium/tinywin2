using System.Text;
using System.Text.RegularExpressions;

var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default
        .WithToolchain(NativeAotToolchain.Net10_0)
        .WithLaunchCount(1)
        .WithWarmupCount(1)
        .WithIterationCount(3));

BenchmarkSwitcher
    .FromAssembly(typeof(LikePatternBenchmarks).Assembly)
    .Run(args, config);

[MemoryDiagnoser]
public class LikePatternBenchmarks {
    private Regex _regex = null!;
    private string[] _serviceNames = [];

    [Params(128, 4096)] public int CandidateCount { get; set; }

    [Params("Service*", "*Network*", "W?32Time")]
    public string Pattern { get; set; } = "Service*";

    [GlobalSetup]
    public void Setup() {
        _serviceNames = Enumerable.Range(0, CandidateCount)
            .Select(index => (index % 3) switch {
                0 => $"Service{index}",
                1 => $"NetworkService{index}",
                _ => $"W{index % 10}32Time"
            })
            .ToArray();
        _regex = LikePattern.ToRegex(Pattern);
    }

    [Benchmark]
    public Regex Compile() => LikePattern.ToRegex(Pattern);

    [Benchmark]
    public int Match() {
        var matches = 0;
        foreach (var serviceName in _serviceNames) {
            matches += _regex.IsMatch(serviceName) ? 1 : 0;
        }

        return matches;
    }

    [Benchmark]
    public int MatchWildcard() {
        var matches = 0;
        foreach (var serviceName in _serviceNames) {
            matches += LikePattern.IsMatch(Pattern, serviceName) ? 1 : 0;
        }

        return matches;
    }
}

[MemoryDiagnoser]
public class PackageRegexBenchmarks {
    private string[] _packageNames = [];
    private Regex _regex = null!;

    [Params(128, 4096)] public int CandidateCount { get; set; }

    [Params("Microsoft-Windows-.*", ".*Edge.*", "Package(One|Two)")]
    public string Pattern { get; set; } = "Microsoft-Windows-.*";

    [GlobalSetup]
    public void Setup() {
        _packageNames = Enumerable.Range(0, CandidateCount)
            .Select(index => (index % 3) switch {
                0 => $"Microsoft-Windows-Feature-{index}",
                1 => $"Microsoft-Edge-Component-{index}",
                _ => $"Package{(index % 2 == 0 ? "One" : "Two")}-{index}"
            })
            .ToArray();
        _regex = new(Pattern, RegexOptions.CultureInvariant);
    }

    [Benchmark]
    public Regex Compile() => new(Pattern, RegexOptions.CultureInvariant);

    [Benchmark]
    public int Match() {
        var matches = 0;
        foreach (var packageName in _packageNames) {
            matches += _regex.IsMatch(packageName) ? 1 : 0;
        }

        return matches;
    }
}

[MemoryDiagnoser]
public class DismListParserBenchmarks {
    private string _output = "";

    [Params(256, 4096)] public int RecordCount { get; set; }

    [GlobalSetup]
    public void Setup() {
        var builder = new StringBuilder(RecordCount * 96);
        for (var index = 0; index < RecordCount; index++) {
            builder.Append("Feature Name : Feature-").Append(index).AppendLine();
            builder.Append("State : Enabled").AppendLine();
            builder.Append("Display Name : Synthetic feature").AppendLine();
            builder.AppendLine();
        }

        _output = builder.ToString();
    }

    [Benchmark]
    public int Parse() => DismListParser.Parse(_output, "Feature Name").Count;
}
