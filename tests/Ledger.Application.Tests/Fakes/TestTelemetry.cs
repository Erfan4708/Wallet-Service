using Ledger.Application.Observability;

namespace Ledger.Application.Tests.Fakes;

/// <summary>
/// One telemetry instance shared by the tests that only need a handler to have
/// one.
/// </summary>
/// <remarks>
/// An <c>ActivitySource</c> and a <c>Meter</c> are process-wide publishers, and
/// creating one per test would leak instruments for no benefit. Tests that
/// actually assert on the signals build their own instance so that what they
/// observe is unambiguously theirs.
/// </remarks>
internal static class TestTelemetry
{
    internal static LedgerTelemetry Instance { get; } = new();
}
