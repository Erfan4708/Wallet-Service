using System.IO.Pipes;

namespace Ledger.Infrastructure.Tests;

/// <summary>
/// Whether a container runtime is reachable on this machine.
/// </summary>
/// <remarks>
/// <para>
/// These tests need a real PostgreSQL server and start one in a container, so
/// they cannot run where Docker is unavailable. Reporting them as skipped rather
/// than failed keeps a missing container runtime — a fact about the environment
/// — from looking like a defect in the code.
/// </para>
/// <para>
/// The danger of that choice is that a skip is quiet. An earlier version of this
/// probe looked for the Docker named pipe with <c>Directory.EnumerateFiles</c>,
/// which .NET normalises from <c>\\.\pipe\</c> to <c>C:\pipe</c> and rejects with
/// a <see cref="DirectoryNotFoundException"/>; being an <see cref="IOException"/>
/// it was swallowed, and nineteen tests reported as skipped on a machine where
/// Docker was running perfectly. The probe below therefore tests the only thing
/// that actually settles the question — whether the endpoint accepts a
/// connection — instead of inferring it from a filename.
/// </para>
/// <para>
/// Set <c>LEDGER_REQUIRE_DOCKER=1</c> to refuse to skip. Continuous integration
/// should always set it: if the tests that prove the PostgreSQL mapping do not
/// run, the mapping is unverified, and that should break the build rather than
/// pass quietly.
/// </para>
/// </remarks>
internal static class DockerAvailability
{
    private const string RequireDockerVariable = "LEDGER_REQUIRE_DOCKER";
    private const int ConnectTimeoutMilliseconds = 1000;

    /// <summary>Docker Desktop's default pipe, then the one its own context uses.</summary>
    private static readonly string[] WindowsPipeNames = ["docker_engine", "dockerDesktopLinuxEngine"];

    internal const string SkipReason =
        "Requires a running Docker daemon: these tests start a real PostgreSQL container. " +
        "Set LEDGER_REQUIRE_DOCKER=1 to fail instead of skipping.";

    internal static bool IsAvailable { get; } = Probe();

    /// <summary>
    /// Whether the caller has demanded these tests run, skip or no skip.
    /// </summary>
    internal static bool IsRequired =>
        Environment.GetEnvironmentVariable(RequireDockerVariable) is "1" or "true" or "True";

    internal static bool ShouldSkip => !IsRequired && !IsAvailable;

    private static bool Probe()
    {
        // An explicit endpoint is the caller telling us where Docker is;
        // Testcontainers honours the same variable, so trust it rather than
        // second-guessing it.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return File.Exists("/var/run/docker.sock");
        }

        return WindowsPipeNames.Any(CanConnect);
    }

    private static bool CanConnect(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            client.Connect(ConnectTimeoutMilliseconds);

            return true;
        }
        catch (TimeoutException)
        {
            return false;
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
        if (DockerAvailability.ShouldSkip)
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
        if (DockerAvailability.ShouldSkip)
        {
            Skip = DockerAvailability.SkipReason;
        }
    }
}
