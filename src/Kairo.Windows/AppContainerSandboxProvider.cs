using Kairo.Core.Tools;

namespace Kairo.Windows;

/// <summary>Makes AppContainer sandboxes from the settings the policy chose.</summary>
public sealed class AppContainerSandboxProvider : ISandboxProvider
{
    public IProcessSandbox Create(SandboxSpec spec)
    {
        if (!AppContainerSandbox.IsSupported)
            throw new PlatformNotSupportedException("AppContainer needs Windows 10 or later");
        return new AppContainerSandbox(new SandboxOptions(spec.Writable, spec.Readable, spec.Network, spec.MemoryLimitMb));
    }
}
