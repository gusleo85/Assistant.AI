using Justina.Core.Domain.Results;

namespace Justina.Expense.Application.Abstractions;

/// <summary>
/// Supplies the bearer credential the Expense API expects for one company.
///
/// The Expense API does not accept a single static key: every call is authenticated with a token minted
/// for the company the expense belongs to. That token is short-lived, so it cannot live in configuration
/// — it has to be fetched, cached and refreshed while the process runs.
///
/// This interface exists so none of that reaches the application layer. Handlers state which tenant they
/// act for and nothing else; whether the credential came from an identity server, a configured key or a
/// stub is an infrastructure question (§31).
/// </summary>
public interface IExpenseAccessTokenProvider
{
    /// <returns>
    /// The token value alone, without its scheme — the caller decides how to present it. A failure is a
    /// <see cref="Result"/>, not an exception: an identity server that is down is an expected condition
    /// the user is told about, not a defect.
    /// </returns>
    Task<Result<string>> GetAsync(ExpenseTenant tenant, CancellationToken cancellationToken);
}
