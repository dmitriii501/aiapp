using Kairo.Core.Config;
using Kairo.Core.Permissions;

namespace Kairo.Core.Tools;

/// <summary>What a sandbox is asked to allow. Everything not listed is closed.</summary>
public sealed record SandboxSpec(IReadOnlyList<string> Writable, IReadOnlyList<string> Readable, bool Network, int MemoryLimitMb);

/// <summary>Makes sandboxes for the platform. The Windows one is <c>AppContainerSandboxProvider</c>; hosts without one pass null.</summary>
public interface ISandboxProvider
{
    IProcessSandbox Create(SandboxSpec spec);
}

/// <summary>
/// Decides, from the user's <c>[sandbox]</c> settings, whether commands of a session or a swarm worker run in the sandbox, and builds
/// it. When the sandbox is wanted but cannot be had, commands do not run at all: a missing fence is never replaced by no fence.
/// </summary>
public sealed class SandboxPolicy(KairoConfig config, ISandboxProvider? provider)
{
    public const int DefaultMemoryMb = 4096;

    /// <summary>
    /// Swarm workers run in the sandbox unless the user turned it off: they act without anyone watching, on text they read from the
    /// repository, so the fence is the default for them. Sessions the user is working in are fenced only when they ask.
    /// </summary>
    public bool Applies(PermissionMode mode, bool swarmWorker) => config.SandboxShell switch
    {
        "off" => false,
        "always" => true,
        "autonomous" => swarmWorker || mode == PermissionMode.Bypass,
        _ => swarmWorker, // "workers", and unset
    };

    /// <summary>The sandbox for a session (<paramref name="swarmWorker"/> false) or a worker, or null when the settings do not ask for one.</summary>
    public IProcessSandbox? For(PermissionMode mode, bool swarmWorker, IEnumerable<string> writable)
    {
        if (!Applies(mode, swarmWorker))
            return null;
        if (provider is null)
            return new UnavailableSandbox("this host has no sandbox");

        try
        {
            return provider.Create(new SandboxSpec(
                [.. writable.Select(Path.GetFullPath)],
                [.. config.SandboxRead.Select(Path.GetFullPath)],
                config.SandboxNetwork ?? false,
                config.SandboxMemoryMb ?? DefaultMemoryMb));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UnavailableSandbox(ex.Message);
        }
    }
}

/// <summary>Stands in when a sandbox was required and could not be made: every command is refused with the reason.</summary>
public sealed class UnavailableSandbox(string reason) : IProcessSandbox
{
    public IReadOnlyList<string> WritablePaths => [];

    public Task<ProcessOutcome> RunAsync(ProcessLaunch launch, CancellationToken ct) =>
        throw new InvalidOperationException($"these commands must run in the sandbox ([sandbox] shell in config.toml; \"off\" turns it off, swarm workers use it unless it is off), but {reason}");
}
