using System.Globalization;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Accepts submissions locally while <see cref="ExpenseApiOptions.Mode"/> is <c>Stub</c>, so the receipt
/// lifecycle can be driven to <c>Submitted</c> without a JustLogin credential or the create-endpoint
/// contract, neither of which exists yet.
///
/// Two things it deliberately does NOT do: it does not pretend to be the real API (every call is logged
/// as stubbed, and the returned id is visibly marked), and it does not swallow the difference — the
/// registration in <c>ExpenseInfrastructureServiceCollectionExtensions</c> refuses to run in Production.
/// </summary>
public sealed class StubExpenseApiClient(ILogger<StubExpenseApiClient> logger) : IExpenseApiClient
{
    public Task<Result<ExpenseSubmissionResult>> SubmitAsync(
        ExpenseSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // Derived from the idempotency key rather than random, so re-submitting the same receipt yields
        // the same reference — the same property the real API's idempotency header is there to give.
        var expenseId = string.Concat(
            "STUB-",
            submission.IdempotencyKey[..12].ToUpperInvariant());

        logger.LogWarning(
            "Expense submission is running in STUB mode: receipt for {Merchant} on {Date} " +
            "({Currency} {Amount}, category {Category}/{CategoryId}, {TaxCount} tax code(s)) was recorded " +
            "locally as {ExpenseId} and was NOT sent to the Expense API",
            submission.Merchant,
            submission.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            submission.Currency,
            submission.Amount,
            submission.Category,
            submission.CategoryId,
            submission.TaxIds.Count,
            expenseId);

        return Task.FromResult(Result.Success(new ExpenseSubmissionResult(expenseId)));
    }
}
