using Xunit;

namespace EasyReasy.Claude.AgentSdk.Tests;

/// <summary>
/// Marks a test that stands up a fake CLI as an executable shell script, which Windows cannot run.
/// Skipped there rather than passed vacuously, so a Windows developer sees that the claim went
/// unchecked instead of seeing a green test that asserted nothing.
/// </summary>
public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Requires a POSIX shell to stand up the fake CLI process.";
    }
}
