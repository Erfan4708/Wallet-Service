namespace Ledger.Infrastructure.Tests;

/// <summary>
/// Whether a container runtime is reachable on this machine.
/// </summary>
/// <remarks>
/// These tests need a real PostgreSQL server and start one in a container, so
/// they cannot run where Docker is not installed. The choice is between failing
/// the suite on such a machine or reporting the tests as skipped; skipped is
/// better, because a missing container runtime is a fact about the environment
/// rather than a defect in the code, and xUnit reports the skip count and reason
/// so the gap stays visible instead of looking like a pass.
/// <para>
/// Continuous integration should treat a skip here as a failure: if the tests
/// that prove the PostgreSQL mapping never ran, the mapping is unverified.
/// </para>
/// </remarks>
internal static class DockerAvailability
{
    internal const string SkipReason =
        "Requires a running Docker daemon: these tests start a real PostgreSQL container.";

    internal static bool IsAvailable { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
            {
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                return Directory
                    .EnumerateFiles(@"\.\pipe\")
                    .Any(pipe => pipe.EndsWith("docker_engine", StringComparison.OrdinalIgnoreCase));
            }

            return File.Exists("/var/run/docker.sock");
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>A fact that is skipped when no container runtime is available.</summary>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}

/// <summary>A theory that is skipped when no container runtime is available.</summary>
public sealed class RequiresDockerTheoryAttribute : TheoryAttribute
{
    public RequiresDockerTheoryAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}
