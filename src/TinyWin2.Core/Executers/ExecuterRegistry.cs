using TinyWin2.Core.Executers.Dism;
using TinyWin2.Core.Executers.Driver;
using TinyWin2.Core.Executers.Fs;
using TinyWin2.Core.Executers.Registry;
using TinyWin2.Core.Native;
using TinyWin2.Core.Plans;

namespace TinyWin2.Core.Executers;

/// <summary>
///     The single dispatch point from resource ids to executer instances
///     using a typed, injectable resource allowlist for tests.
/// </summary>
public sealed class ExecuterRegistry {
    private readonly Dictionary<string, IExecuter> _byResource;

    public ExecuterRegistry(IEnumerable<IExecuter> executers) {
        _byResource = new(StringComparer.Ordinal);
        foreach (var executer in executers) {
            if (!_byResource.TryAdd(executer.Resource, executer)) {
                throw new InvalidOperationException(
                    $"duplicate executer registration for resource '{executer.Resource}'.");
            }
        }
    }

    public ExecuterRegistry(IProcessRunner runner)
        : this(CreateBuiltins(runner)) {
    }

    private static IEnumerable<IExecuter> CreateBuiltins(IProcessRunner runner) {
        yield return new RegistryValueExecuter(runner);
        yield return new RegistryServiceExecuter(runner);
        yield return new FeatureExecuter(runner);
        yield return new CapabilityExecuter(runner);
        yield return new PackageExecuter(runner);
        yield return new ComponentStoreExecuter(runner);
        yield return new AppxProvisionedExecuter(runner);
        yield return new AppxSystemExecuter(runner);
        yield return new DriverStoreExecuter(runner);
        yield return new FsPathExecuter(runner);
    }

    public IExecuter Get(string resource) =>
        _byResource.TryGetValue(resource, out var executer)
            ? executer
            : throw new ExecException($"no executer registered for resource '{resource}'.");

    /// <summary>Validates that every operation maps to a registered resource and valid action/spec.</summary>
    public void ValidateBuildPlan(BuildPlan plan) {
        foreach (var step in plan.Steps) {
            var operation = step.Plan.Operation;
            Get(operation.Resource).Validate(operation);
        }
    }
}
