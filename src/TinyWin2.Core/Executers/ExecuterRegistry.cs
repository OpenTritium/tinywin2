using TinyWin2.Core.Executers.Dism;
using TinyWin2.Core.Executers.Driver;
using TinyWin2.Core.Executers.Fs;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Executers;

/// <summary>
/// The single dispatch point from resource ids to executer instances
/// (v1's handler allowlist, but typed and injectable for tests).
/// </summary>
public sealed class ExecuterRegistry {
    private readonly Dictionary<string, IExecuter> _byResource;

    public ExecuterRegistry(IEnumerable<IExecuter> executers) {
        _byResource = new Dictionary<string, IExecuter>(StringComparer.Ordinal);
        foreach (var executer in executers) {
            if (!_byResource.TryAdd(executer.Resource, executer)) {
                throw new InvalidOperationException($"duplicate executer registration for resource '{executer.Resource}'.");
            }
        }
    }

    public ExecuterRegistry(IProcessRunner runner)
        : this(CreateBuiltins(runner)) {
    }

    /// <summary>For dependency-free environments (preview/testing): no native calls are made until run.</summary>
    public ExecuterRegistry()
        : this(new ProcessRunner()) {
    }

    public static IEnumerable<IExecuter> CreateBuiltins(IProcessRunner runner) {
        yield return new Registry.RegistryValueExecuter(runner);
        yield return new Registry.RegistryServiceExecuter(runner);
        yield return new FeatureExecuter(runner);
        yield return new CapabilityExecuter(runner);
        yield return new PackageExecuter(runner);
        yield return new ComponentStoreExecuter(runner);
        yield return new AppxProvisionedExecuter(runner);
        yield return new DriverStoreExecuter(runner);
        yield return new FsPathExecuter(runner);
    }

    public IExecuter Get(string resource) =>
        _byResource.TryGetValue(resource, out var executer)
            ? executer
            : throw new ExecException($"no executer registered for resource '{resource}'.");

    /// <summary>Validates that every exec in the plan maps to a registered resource.</summary>
    public void ValidateBuildPlan(BuildPlan plan) {
        var unknown = plan.Steps
            .SelectMany(step => step.Plans)
            .SelectMany(resolved => resolved.Execs)
            .Select(exec => exec.Resource)
            .Where(resource => !_byResource.ContainsKey(resource))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0) {
            throw new ExecException($"unknown resources in build plan: {string.Join(", ", unknown)}");
        }
    }
}
