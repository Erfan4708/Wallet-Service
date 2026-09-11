using Ledger.Application.Abstractions;
using Ledger.Application.Observability;
using Ledger.Application.Exceptions;
using Ledger.Domain.Entities;
using Ledger.Domain.Enums;

namespace Ledger.Application.Accounts.CreateAccount;

/// <summary>
/// Opens a new account.
/// </summary>
/// <remarks>
/// A plain class with a plain method, registered directly in the container. It
/// deliberately implements no marker interface and sits behind no mediator:
/// there is exactly one caller and one implementation, so an abstraction between
/// them would add indirection without removing a dependency.
/// </remarks>
public sealed class CreateAccountHandler
{
    private readonly IAccountRepository _accounts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly LedgerTelemetry _telemetry;

    public CreateAccountHandler(
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        LedgerTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(telemetry);

        _accounts = accounts;
        _unitOfWork = unitOfWork;
        _telemetry = telemetry;
    }

    /// <exception cref="ValidationException">The command is malformed.</exception>
    /// <exception cref="ConflictException">An account with that identifier already exists.</exception>
    public async Task<AccountSummary> HandleAsync(
        CreateAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var activity = _telemetry.StartActivity("ledger.account.open");
        activity?.SetTag("ledger.account_id", command.AccountId);

        Validate(command);

        // Checking first gives a clear 409 for the common case. It is not a
        // guarantee: two concurrent requests can both pass this check. The
        // authoritative defence is the primary key, whose violation the unit of
        // work translates into the same conflict.
        var existing = await _accounts.GetByIdAsync(command.AccountId, cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException($"Account '{command.AccountId}' already exists.");
        }

        var account = Account.Open(command.AccountId, command.Currency);

        await _accounts.AddAsync(account, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _telemetry.RecordAccountOpened(command.Currency);

        return AccountSummary.From(account);
    }

    /// <remarks>
    /// The domain rejects these values too, but it does so with
    /// <see cref="ArgumentException"/>, which means "the caller has a bug" and
    /// is not something to translate into a helpful response. Validating here
    /// turns bad input into a clear 400 while leaving the domain's guard in
    /// place as the last line of defence.
    /// </remarks>
    private static void Validate(CreateAccountCommand command)
    {
        var errors = new Dictionary<string, string[]>();

        if (command.AccountId == Guid.Empty)
        {
            errors[nameof(command.AccountId)] = ["An account identifier is required."];
        }

        if (!Enum.IsDefined(command.Currency))
        {
            errors[nameof(command.Currency)] = ["Unknown currency."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
