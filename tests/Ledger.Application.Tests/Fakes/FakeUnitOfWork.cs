using Ledger.Application.Abstractions;

namespace Ledger.Application.Tests.Fakes;

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public FakeUnitOfWork(List<string>? callLog = null) => CallLog = callLog ?? [];

    public List<string> CallLog { get; }

    public int SaveCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        CallLog.Add(nameof(SaveChangesAsync));
        SaveCount++;

        return Task.CompletedTask;
    }
}
