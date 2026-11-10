using Tokonomy.Core.Config;
using Tokonomy.Runtime.Git;
using Tokonomy.Runtime.Process;

namespace Tokonomy.Runtime.Gates.Isolation;

/// <summary>
/// Turns <c>gate_execution</c> into a provider. There is no default and no fallback: a missing section, an unknown
/// provider or an unusable toolchain is an error, and <c>host-unsafe</c> is only ever used when it is written down.
/// </summary>
public static class GateExecutionFactory
{
    public const string MissingSection =
        "policy.yaml has no gate_execution section. Gates run code the worker wrote, so Tokonomy will not run them until you " +
        "choose where: `gate_execution: { provider: windows-sandbox }` (isolated; see docs/config.md) or " +
        "`gate_execution: { provider: host-unsafe }` (runs gates on this machine with your privileges, as before).";

    /// <summary>Every command a run will execute through the provider: prepare steps and command gates.</summary>
    public static IEnumerable<IReadOnlyList<string>> Commands(Policy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Prepare.Concat(policy.Gates.Where(g => g.Type == GateConfig.CommandType))
            .Where(g => g.Cmd.Count > 0)
            .Select(g => (IReadOnlyList<string>)g.Cmd);
    }

    /// <summary>Throws <see cref="GateIsolationUnavailableException"/> with a diagnostic when no usable provider is configured.</summary>
    public static IGateExecutionProvider Create(
        GateExecutionConfig? config, IProcessRunner runner, string repoRoot, IEnumerable<IReadOnlyList<string>> commands,
        IReadOnlyCollection<string> alsoRemoveEnv, string? worktreeRoot = null, string? storeRoot = null)
    {
        if (config is null)
        {
            throw new GateIsolationUnavailableException(MissingSection);
        }

        switch (config.Provider)
        {
            case GateExecutionConfig.HostUnsafeProvider:
                return new HostUnsafeGateProvider(runner, alsoRemoveEnv, config.ArtifactLimits);

            case GateExecutionConfig.WindowsSandboxProvider:
                var root = storeRoot ?? GateEnvStore.DefaultRoot();
                if (storeRoot is null)
                {
                    var problems = WorktreeRoot.Validate(root, repoRoot);
                    if (problems.Count > 0)
                    {
                        throw new GateIsolationUnavailableException($"gate environment folder {root ?? "(unknown)"} is unsafe: {string.Join("; ", problems)}");
                    }
                }

                var refuse = new[] { root!, WorktreeRoot.Effective(worktreeRoot, repoRoot) ?? "" }.Where(p => p.Length > 0);
                var (toolchains, toolProblems) = Toolchains.Resolve(config, commands, repoRoot, refuse);
                if (toolProblems.Count > 0)
                {
                    throw new GateIsolationUnavailableException($"cannot prepare the gate environment: {string.Join("; ", toolProblems)}");
                }

                return new WindowsSandboxProvider(runner, config, new GateEnvStore(root!), toolchains);

            default:
                throw new GateIsolationUnavailableException(
                    $"gate_execution.provider '{config.Provider}' is not supported; use '{GateExecutionConfig.WindowsSandboxProvider}' or '{GateExecutionConfig.HostUnsafeProvider}'.");
        }
    }
}
